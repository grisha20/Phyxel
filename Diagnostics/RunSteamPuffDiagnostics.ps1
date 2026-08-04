param(
    # A 100% brush and the deterministic per-cell hash yield the same path for
    # every acceptance seed.  This scenario is one measurement, not a sample
    # distribution.
    [ValidateRange(1, 1)]
    [int]$Runs = 1,
    [string]$ArtifactSuffix = ''
)

$ErrorActionPreference = 'Stop'
$culture = [System.Globalization.CultureInfo]::InvariantCulture
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Add-Sample([hashtable]$target, [string]$name, [double]$value) {
    if (-not $target.ContainsKey($name)) {
        $target[$name] = [System.Collections.Generic.List[double]]::new()
    }
    $target[$name].Add($value)
}

function Write-Summary([hashtable]$values, [string]$state) {
    foreach ($name in $values.Keys | Sort-Object) {
        [double[]]$samples = $values[$name].ToArray()
        $mean = ($samples | Measure-Object -Average).Average
        $variance = 0.0
        foreach ($sample in $samples) { $variance += [math]::Pow($sample - $mean, 2) }
        $sigma = [math]::Sqrt($variance / $samples.Length)
        Write-Host ("PHYXEL_STEAM_PUFF_SUMMARY state={0} {1} mean={2:F6} stddev={3:F6} min={4:F6} max={5:F6}" -f `
            $state, $name, $mean, $sigma, ($samples | Measure-Object -Minimum).Minimum, ($samples | Measure-Object -Maximum).Maximum)
    }
}

dotnet build Phyxel.sln -c Debug --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

foreach ($state in @(
    [pscustomobject]@{ Name = 'air-on'; Enabled = '1' },
    [pscustomobject]@{ Name = 'air-off'; Enabled = '0' }
)) {
    $values = @{}
    $artifactRoot = Join-Path $repoRoot ("artifacts\steam-puff-" + $state.Name + $ArtifactSuffix)
    New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
    $rawReport = Join-Path $artifactRoot 'steam-puff-diagnostics-runs.log'
    Set-Content -Path $rawReport -Value ''

    for ($run = 1; $run -le $Runs; $run++) {
        $seed = 71000 + $run
        $env:PHYXEL_ACCEPTANCE_MODE = 'steam_puff'
        $env:PHYXEL_ACCEPTANCE_RUN_SEED = $seed.ToString($culture)
        $env:PHYXEL_ACCEPTANCE_SCALE = '0.25'
        $env:PHYXEL_ACCEPTANCE_TARGET_FPS = '60'
        $env:PHYXEL_ACCEPTANCE_CAPTURE_FRAME = '600'
        $env:PHYXEL_ACCEPTANCE_AIR = $state.Enabled
        $env:PHYXEL_ARTIFACT_DIR = Join-Path $artifactRoot ("run-" + $run)
        $output = & dotnet run --project Phyxel.csproj -c Debug --no-build 2>&1
        $exitCode = $LASTEXITCODE
        Add-Content -Path $rawReport -Value ("=== run $run seed=$seed air=$($state.Enabled) ===")
        $output | Add-Content -Path $rawReport

        $line = $output | Where-Object { $_ -like 'PHYXEL_STEAM_PUFF *' } | Select-Object -Last 1
        if (-not $line) { throw "steam_puff $($state.Name) run $run produced no diagnostic line" }
        Write-Host "PHYXEL_STEAM_PUFF_RUN state=$($state.Name) run=$run seed=$seed exitCode=$exitCode $($line.Substring('PHYXEL_STEAM_PUFF '.Length))"
        foreach ($match in [regex]::Matches($line, '(?:^|\s)([A-Za-z][A-Za-z0-9]*)=(-?[0-9]+(?:\.[0-9]+)?)')) {
            Add-Sample $values $match.Groups[1].Value ([double]::Parse($match.Groups[2].Value, $culture))
        }
    }

    Write-Summary $values $state.Name
    Write-Host "PHYXEL_STEAM_PUFF_RAW_REPORT state=$($state.Name) path=$rawReport"
}
