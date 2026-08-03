param(
    [ValidateRange(3, 3)]
    [int]$Runs = 3
)

$ErrorActionPreference = 'Stop'
$culture = [System.Globalization.CultureInfo]::InvariantCulture
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactDirectory = Join-Path $repoRoot 'artifacts\fire-open-air'
New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null

function Add-Sample([hashtable]$target, [string]$name, [double]$value) {
    if (-not $target.ContainsKey($name)) {
        $target[$name] = [System.Collections.Generic.List[double]]::new()
    }
    $target[$name].Add($value)
}

function Get-Statistics([double[]]$samples) {
    $mean = ($samples | Measure-Object -Average).Average
    $variance = 0.0
    foreach ($sample in $samples) {
        $variance += [math]::Pow($sample - $mean, 2)
    }
    [pscustomobject]@{
        Mean = $mean
        StandardDeviation = [math]::Sqrt($variance / $samples.Length)
        Minimum = ($samples | Measure-Object -Minimum).Minimum
        Maximum = ($samples | Measure-Object -Maximum).Maximum
    }
}

function Write-Summary([hashtable]$source, [string]$prefix) {
    foreach ($name in $source.Keys | Sort-Object) {
        $stats = Get-Statistics $source[$name].ToArray()
        Write-Host ("PHYXEL_FIRE_OPEN_{0} {1} mean={2:F6} stddev={3:F6} min={4:F6} max={5:F6}" -f `
            $prefix, $name, $stats.Mean, $stats.StandardDeviation, $stats.Minimum, $stats.Maximum)
    }
}

dotnet build Phyxel.sln -c Debug --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$values = @{}
$profiles = @{}
$rawReport = Join-Path $artifactDirectory 'fire-open-air-diagnostics-runs.log'
Set-Content -Path $rawReport -Value ''

for ($run = 1; $run -le $Runs; $run++) {
    $runSeed = 71000 + $run
    $env:PHYXEL_ACCEPTANCE_MODE = 'fire_open'
    $env:PHYXEL_ACCEPTANCE_RUN_SEED = $runSeed.ToString($culture)
    $env:PHYXEL_ACCEPTANCE_SCALE = '0.25'
    $env:PHYXEL_ACCEPTANCE_TARGET_FPS = '60'
    $env:PHYXEL_ACCEPTANCE_CAPTURE_FRAME = '359'
    $env:PHYXEL_ARTIFACT_DIR = Join-Path $artifactDirectory ("run-" + $run)
    $output = & dotnet run --project Phyxel.csproj -c Debug --no-build 2>&1
    $exitCode = $LASTEXITCODE
    Add-Content -Path $rawReport -Value ("=== run $run seed=$runSeed ===")
    $output | Add-Content -Path $rawReport

    $line = $output | Where-Object { $_ -like 'PHYXEL_FIRE_OPEN *' } | Select-Object -Last 1
    if (-not $line) { throw "fire_open run $run produced no diagnostic line" }
    $scalarPart = ($line -split ' airProfiles=', 2)[0]
    Write-Host "PHYXEL_FIRE_OPEN_AIR_RUN run=$run seed=$runSeed exitCode=$exitCode $($scalarPart.Substring('PHYXEL_FIRE_OPEN '.Length))"
    foreach ($match in [regex]::Matches($scalarPart, '(?:^|\s)([A-Za-z][A-Za-z0-9]*)=(-?[0-9]+(?:\.[0-9]+)?)')) {
        Add-Sample $values $match.Groups[1].Value ([double]::Parse($match.Groups[2].Value, $culture))
    }

    if ($line -notmatch ' airProfiles=(.+?) airPressure=') {
        throw "fire_open run $run produced no Air profile"
    }
    $profileText = $Matches[1]
    foreach ($heightMatch in [regex]::Matches($profileText, 'h(\d+)\[y\d+\]\(([^)]*)\)')) {
        $height = $heightMatch.Groups[1].Value
        foreach ($sample in $heightMatch.Groups[2].Value -split ',') {
            if ($sample -notmatch '^d(-?\d+):(-?[0-9]+(?:\.[0-9]+)?)/(-?[0-9]+(?:\.[0-9]+)?)/(-?[0-9]+(?:\.[0-9]+)?)$') {
                throw "Invalid Air profile sample: $sample"
            }
            $distance = $Matches[1]
            Add-Sample $profiles ("h${height}_d${distance}_vx") ([double]::Parse($Matches[2], $culture))
            Add-Sample $profiles ("h${height}_d${distance}_vy") ([double]::Parse($Matches[3], $culture))
            Add-Sample $profiles ("h${height}_d${distance}_pressure") ([double]::Parse($Matches[4], $culture))
        }
    }
}

Write-Summary $values 'AIR_SUMMARY'
Write-Summary $profiles 'AIR_PROFILE_SUMMARY'

$profileRows = foreach ($name in $profiles.Keys) {
    if ($name -notmatch '^h(\d+)_d(-?\d+)_(vx|vy|pressure)$') { continue }
    $stats = Get-Statistics $profiles[$name].ToArray()
    [pscustomobject]@{
        HeightAboveSource = [int]$Matches[1]
        DistanceFromAxisCoarseCells = [int]$Matches[2]
        Component = $Matches[3]
        Mean = [math]::Round($stats.Mean, 6)
        StandardDeviation = [math]::Round($stats.StandardDeviation, 6)
        Minimum = [math]::Round($stats.Minimum, 6)
        Maximum = [math]::Round($stats.Maximum, 6)
    }
}
$profileCsv = Join-Path $artifactDirectory 'air-profiles-summary.csv'
$profileRows |
    Sort-Object HeightAboveSource, DistanceFromAxisCoarseCells, Component |
    Export-Csv -NoTypeInformation -Encoding utf8 -Path $profileCsv
Write-Host "PHYXEL_FIRE_OPEN_AIR_PROFILE_CSV $profileCsv"
Write-Host "PHYXEL_FIRE_OPEN_AIR_RAW_REPORT $rawReport"
