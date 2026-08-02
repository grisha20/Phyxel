param(
    [ValidateRange(3, 3)]
    [int]$Runs = 3
)

$ErrorActionPreference = 'Stop'
$culture = [System.Globalization.CultureInfo]::InvariantCulture
$artifactDirectory = Join-Path $PSScriptRoot '..\artifacts\powder-toy-acceptance'
New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null

dotnet build Phyxel.sln -c Debug --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

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
        $minimum = ($samples | Measure-Object -Minimum).Minimum
        $maximum = ($samples | Measure-Object -Maximum).Maximum
        $variance = 0.0
        foreach ($sample in $samples) { $variance += [math]::Pow($sample - $mean, 2) }
        $standardDeviation = [math]::Sqrt($variance / $samples.Length)
        Write-Host ("{0} {1} mean={2:F3} stddev={3:F3} min={4:F3} max={5:F3}" -f $prefix, $name, $mean, $standardDeviation, $minimum, $maximum)
    }
}

function Run-Scenario([string]$mode, [string]$reportPrefix, [string]$traceName) {
    $values = @{}
    $pressure = @{}
    $rawReport = Join-Path $artifactDirectory ($mode + '-air-feedback-runs.log')
    Set-Content -Path $rawReport -Value ''

    for ($run = 1; $run -le $Runs; $run++) {
        $env:PHYXEL_ACCEPTANCE_MODE = $mode
        $env:PHYXEL_ACCEPTANCE_SCALE = '0.25'
        $env:PHYXEL_ACCEPTANCE_TARGET_FPS = '60'
        $env:PHYXEL_ACCEPTANCE_CAPTURE_FRAME = '359'
        $output = & dotnet run --project Phyxel.csproj -c Debug --no-build 2>&1
        $exitCode = $LASTEXITCODE
        Add-Content -Path $rawReport -Value ("=== run $run ===")
        $output | Add-Content -Path $rawReport

        $line = $output | Where-Object { $_ -like ($reportPrefix + ' *') } | Select-Object -Last 1
        if (-not $line) { throw "$mode run $run produced no $reportPrefix line" }
        $scalarPart = ($line -split ' airPressure=', 2)[0]
        Write-Host "$reportPrefix`_RUN run=$run exitCode=$exitCode $($scalarPart.Substring(($reportPrefix + ' ').Length))"
        foreach ($match in [regex]::Matches($scalarPart, '(?:^|\s)([A-Za-z][A-Za-z0-9]*)=(-?[0-9]+(?:\.[0-9]+)?)')) {
            Add-Sample $values $match.Groups[1].Value ([double]::Parse($match.Groups[2].Value, $culture))
        }
        if ($line -match 'airPressure=y\d+;(.+)$') {
            foreach ($sample in $Matches[1] -split ';') {
                if ($sample -match '^(\d+):(-?[0-9]+(?:\.[0-9]+)?)$') {
                    Add-Sample $pressure ("airPressureX" + $Matches[1]) ([double]::Parse($Matches[2], $culture))
                }
            }
        }

        $trace = Join-Path $artifactDirectory $traceName
        if (-not (Test-Path $trace)) { throw "$mode run $run did not produce $traceName" }
        Copy-Item -LiteralPath $trace -Destination (Join-Path $artifactDirectory ($mode + '-pressure-trace-run' + $run + '.csv')) -Force
    }

    Write-Summary $values ($reportPrefix + '_SUMMARY')
    Write-Summary $pressure ($reportPrefix + '_PRESSURE_SUMMARY')
    Write-Host "$reportPrefix`_RAW_REPORT $rawReport"
}

Run-Scenario 'fire_obstacle' 'PHYXEL_FIRE_OBSTACLE' 'fire-obstacle-pressure-trace.csv'
Run-Scenario 'fire_open' 'PHYXEL_FIRE_OPEN' 'fire-open-pressure-trace.csv'
