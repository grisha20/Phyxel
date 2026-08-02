param(
    [ValidateRange(3, 3)]
    [int]$Runs = 3,
    [ValidateSet('targetx-off', 'targetx-on')]
    [string]$Label = 'targetx-off'
)

$ErrorActionPreference = 'Stop'
$culture = [System.Globalization.CultureInfo]::InvariantCulture

dotnet build Phyxel.sln -c Debug --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$values = @{}
$pressure = @{}
$artifactDirectory = Join-Path $PSScriptRoot ("..\artifacts\fire-obstacle-$Label")
New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
$rawReport = Join-Path $artifactDirectory 'fire-obstacle-diagnostics-runs.log'
Set-Content -Path $rawReport -Value ''

function Add-Sample([hashtable]$target, [string]$name, [double]$value) {
    if (-not $target.ContainsKey($name)) { $target[$name] = [System.Collections.Generic.List[double]]::new() }
    $target[$name].Add($value)
}

for ($run = 1; $run -le $Runs; $run++) {
    $runSeed = 71000 + $run
    $env:PHYXEL_ACCEPTANCE_MODE = 'fire_obstacle'
    $env:PHYXEL_ACCEPTANCE_RUN_SEED = $runSeed.ToString($culture)
    $env:PHYXEL_ACCEPTANCE_SCALE = '0.25'
    $env:PHYXEL_ACCEPTANCE_TARGET_FPS = '60'
    $env:PHYXEL_ACCEPTANCE_CAPTURE_FRAME = '359'
    $env:PHYXEL_ARTIFACT_DIR = Join-Path $artifactDirectory ("run-$run")
    $output = & dotnet run --project Phyxel.csproj -c Debug --no-build 2>&1
    Add-Content -Path $rawReport -Value ("=== run $run seed=$runSeed ===")
    $output | Add-Content -Path $rawReport
    $runExitCode = $LASTEXITCODE

    $line = $output | Where-Object { $_ -like 'PHYXEL_FIRE_OBSTACLE *' } | Select-Object -Last 1
    if (-not $line) { throw "fire_obstacle run $run produced no diagnostic line" }
    $scalarPart = ($line -split ' fireHistogram=', 2)[0]
    Write-Host "PHYXEL_FIRE_OBSTACLE_RUN run=$run seed=$runSeed exitCode=$runExitCode $($scalarPart.Substring('PHYXEL_FIRE_OBSTACLE '.Length))"

    foreach ($match in [regex]::Matches($scalarPart, '(?:^|\s)([A-Za-z][A-Za-z0-9]*)=(-?[0-9]+(?:\.[0-9]+)?)')) {
        Add-Sample $values $match.Groups[1].Value ([double]::Parse($match.Groups[2].Value, $culture))
    }

    if ($line -match 'airPressure=y\d+;(.+?) globalMinX=') {
        foreach ($sample in $Matches[1] -split ';') {
            if ($sample -match '^(\d+):(-?[0-9]+(?:\.[0-9]+)?)$') {
                Add-Sample $pressure ("airPressureX" + $Matches[1]) ([double]::Parse($Matches[2], $culture))
            }
        }
    }
}

function Write-Summary([hashtable]$source, [string]$prefix) {
    foreach ($name in $source.Keys | Sort-Object) {
        [double[]]$samples = $source[$name].ToArray()
        $mean = ($samples | Measure-Object -Average).Average
        $minimum = ($samples | Measure-Object -Minimum).Minimum
        $maximum = ($samples | Measure-Object -Maximum).Maximum
        $variance = 0.0
        foreach ($sample in $samples) { $variance += [math]::Pow($sample - $mean, 2) }
        $standardDeviation = [math]::Sqrt($variance / $samples.Length)
        Write-Host ("PHYXEL_FIRE_OBSTACLE_{0} {1} mean={2:F3} stddev={3:F3} min={4:F3} max={5:F3}" -f $prefix, $name, $mean, $standardDeviation, $minimum, $maximum)
    }
}

Write-Summary $values 'SUMMARY'
Write-Summary $pressure 'PRESSURE_SUMMARY'
Write-Host "PHYXEL_FIRE_OBSTACLE_RAW_REPORT $rawReport"
