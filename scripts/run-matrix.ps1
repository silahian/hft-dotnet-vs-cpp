# Corrected measurement protocol (replaces run-all.ps1).
#
# Three defects in the previous protocol, all found by adversarial review:
#
#   D1  The observation was a series inside one process. The 25 series in a process are
#       thermally autocorrelated, so their stdev measured jitter, not reproducibility.
#       Re-running the SAME binary gave medians spanning 6.5% while the published
#       cross-language claim was 1.4%. Fix: the OBSERVATION IS ONE PROCESS LAUNCH, and
#       we take many independent launches.
#
#   D3  Config order was fixed, C++ always first and C# always last, so any position
#       effect was perfectly confounded with language. A C++ process run second measured
#       12% slower than the same process run first. Fix: randomized order.
#
#   D3b The C# process ran 8 benchmarks and the C++ process 4, so C# carried ~5x the
#       sustained thermal load and its later benchmarks were measured deeper into the
#       heat curve. Fix: --only, so every process runs exactly ONE benchmark.
#
#   powershell -File scripts\run-matrix.ps1 [-Core 11] [-Launches 7]

param(
    [int]$Core     = 11,
    [int]$Warmup   = 40,
    [int]$Series   = 15,
    [int]$Launches = 7,
    [int]$Cooldown = 6,
    [int]$Seed     = 20260806
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$results = Join-Path $root 'results'
New-Item -ItemType Directory -Force -Path $results | Out-Null

$cppBench = @('orderbook_l2', 'fix_parse', 'fix_parse_simd', 'fix_parse_nosum')
$csBench  = @('orderbook_l2_safe', 'orderbook_l2_opt', 'orderbook_l2_unsafe',
              'fix_parse_safe', 'fix_parse_safe_simd', 'fix_parse_safe_simd_create',
              'fix_parse_opt', 'fix_parse_opt_simd', 'fix_parse_nosum')

$csExe  = "$root\src\csharp\Bench.Runner\bin\Release\net10.0\bench-csharp.exe"
$aotExe = "$root\build\aot\bench-csharp.exe"

$configs = @(
    @{ Tag = 'cpp-msvc1444'; Exe = "$root\build\cpp-msvc1444\bench-cpp.exe"; Bench = $cppBench; Env = $null },
    @{ Tag = 'cpp-msvc1451'; Exe = "$root\build\cpp-msvc1451\bench-cpp.exe"; Bench = $cppBench; Env = $null },
    @{ Tag = 'jit-default';  Exe = $csExe;  Bench = $csBench; Env = $null },
    @{ Tag = 'jit-tc0';      Exe = $csExe;  Bench = $csBench; Env = @{ DOTNET_TieredCompilation = '0' } },
    @{ Tag = 'aot';          Exe = $aotExe; Bench = $csBench; Env = $null }
)

foreach ($c in $configs) {
    if (-not (Test-Path $c.Exe)) { throw "missing binary for $($c.Tag): $($c.Exe)" }
}

# Build the full task list, then shuffle it once with a fixed seed so the order is
# randomized but the whole run is reproducible.
$tasks = foreach ($c in $configs) {
    foreach ($b in $c.Bench) {
        for ($i = 1; $i -le $Launches; $i++) {
            [pscustomobject]@{ Config = $c; Bench = $b; Launch = $i }
        }
    }
}
$tasks = $tasks | Get-Random -Count ([int]::MaxValue) -SetSeed $Seed

Write-Host "core=$Core warmup=$Warmup series=$Series launches=$Launches cooldown=${Cooldown}s"
Write-Host "tasks=$($tasks.Count) (randomized, seed=$Seed)  one benchmark per process"

$n = 0
foreach ($t in $tasks) {
    $n++
    $c = $t.Config
    $out = Join-Path $results ("proc-{0}-{1}-{2}.json" -f $c.Tag, $t.Bench, $t.Launch)

    if ($c.Env) { foreach ($k in $c.Env.Keys) { Set-Item -Path "Env:$k" -Value $c.Env[$k] } }
    try {
        $line = & $c.Exe run --core $Core --warmup $Warmup --series $Series `
                             --only $t.Bench --tag $c.Tag --out $out 2>&1 |
                Select-String 'median='
        if ($LASTEXITCODE -ne 0) { throw "$($c.Tag)/$($t.Bench) exited $LASTEXITCODE" }
        Write-Host ("[{0,4}/{1}] {2,-14} {3,-28} {4}" -f $n, $tasks.Count, $c.Tag, $t.Bench, ($line -join ' ').Trim())
    }
    finally {
        if ($c.Env) { foreach ($k in $c.Env.Keys) { Remove-Item -Path "Env:$k" -ErrorAction SilentlyContinue } }
    }

    Start-Sleep -Seconds $Cooldown
}

Write-Host ""
Write-Host "done -> $results"
