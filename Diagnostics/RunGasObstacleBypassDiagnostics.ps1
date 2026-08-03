param(
    [ValidateRange(3, 3)]
    [int]$Runs = 3
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

function Write-Summary([hashtable]$source, [string]$prefix) {
    foreach ($name in $source.Keys | Sort-Object) {
        [double[]]$samples = $source[$name].ToArray()
        $mean = ($samples | Measure-Object -Average).Average
        $variance = 0.0
        foreach ($sample in $samples) { $variance += [math]::Pow($sample - $mean, 2) }
        $standardDeviation = [math]::Sqrt($variance / $samples.Length)
        Write-Host ("PHYXEL_GAS_BYPASS_{0} {1} mean={2:F6} stddev={3:F6}" -f `
            $prefix, $name, $mean, $standardDeviation)
    }
}

function Run-Scenario([string]$mode) {
    $values = @{}
    $artifactDirectory = Join-Path $repoRoot ("artifacts\\gas-obstacle-bypass-" + $mode)
    New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
    $rawReport = Join-Path $artifactDirectory 'runs.log'
    Set-Content -Path $rawReport -Value ''

    for ($run = 1; $run -le $Runs; $run++) {
        $runSeed = 71000 + $run
        $env:PHYXEL_ACCEPTANCE_MODE = $mode
        $env:PHYXEL_ACCEPTANCE_RUN_SEED = $runSeed.ToString($culture)
        $env:PHYXEL_ACCEPTANCE_SCALE = '0.25'
        $env:PHYXEL_ACCEPTANCE_TARGET_FPS = '60'
        $env:PHYXEL_ACCEPTANCE_CAPTURE_FRAME = '359'
        $env:PHYXEL_GAS_BYPASS_TRACE = '1'
        $env:PHYXEL_ARTIFACT_DIR = Join-Path $artifactDirectory ("run-" + $run)
        $output = & dotnet run --project Phyxel.csproj -c Debug --no-build 2>&1
        $exitCode = $LASTEXITCODE
        Add-Content -Path $rawReport -Value ("=== run $run seed=$runSeed ===")
        $output | Add-Content -Path $rawReport

        $prefix = if ($mode -eq 'fire_open') { 'PHYXEL_FIRE_OPEN' } else { 'PHYXEL_FIRE_OBSTACLE' }
        $line = $output | Where-Object { $_ -like ($prefix + ' *') } | Select-Object -Last 1
        if (-not $line) { throw "$mode run $run produced no $prefix line" }
        $scalarPart = if ($mode -eq 'fire_open') { ($line -split ' fireColumnsH20=', 2)[0] } else { ($line -split ' fireHistogram=', 2)[0] }
        foreach ($match in [regex]::Matches($scalarPart, '(?:^|\s)([A-Za-z][A-Za-z0-9]*)=(-?[0-9]+(?:\.[0-9]+)?)')) {
            Add-Sample $values $match.Groups[1].Value ([double]::Parse($match.Groups[2].Value, $culture))
        }

        $bypass = $output | Where-Object { $_ -like 'PHYXEL_GAS_OBSTACLE_BYPASS *' } | Select-Object -Last 1
        if (-not $bypass) { throw "$mode run $run produced no bypass trace summary" }
        foreach ($match in [regex]::Matches($bypass, '(?:^|\s)(blocked|xOnly|yOnly|diagonal|stayed)=([0-9]+)')) {
            Add-Sample $values ('bypass' + $match.Groups[1].Value) ([double]::Parse($match.Groups[2].Value, $culture))
        }
        Write-Host "PHYXEL_GAS_BYPASS_RUN mode=$mode run=$run seed=$runSeed exitCode=$exitCode $scalarPart $bypass"
    }

    Write-Summary $values ($mode + '_SUMMARY')
    Write-Host "PHYXEL_GAS_BYPASS_RAW_REPORT mode=$mode path=$rawReport"
}

dotnet build Phyxel.sln -c Debug --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Run-Scenario 'fire_open'
Run-Scenario 'fire_obstacle'
