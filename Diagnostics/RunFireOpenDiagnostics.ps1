param(
    [ValidateRange(3, 3)]
    [int]$Runs = 3
)

$ErrorActionPreference = 'Stop'
$culture = [System.Globalization.CultureInfo]::InvariantCulture
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$shaderPath = Join-Path $repoRoot 'Content\Shaders\AirSimulation.hlsl'
$originalShader = [System.IO.File]::ReadAllText($shaderPath)
$targetXOn = '        float targetX = meanVelocityX * (AirParticleDrag / (1.0 - AirParticleLoss));'
$targetXOff = '        float targetX = 0.0;'

function Set-TargetX([bool]$enabled) {
    $replacement = if ($enabled) { $targetXOn } else { $targetXOff }
    if (-not [regex]::IsMatch($originalShader, '(?m)^\s*float targetX = .*$')) {
        throw 'Could not find the targetX declaration in AirSimulation.hlsl.'
    }
    $updated = [regex]::Replace(
        $originalShader,
        '(?m)^\s*float targetX = .*;$',
        $replacement)
    [System.IO.File]::WriteAllText($shaderPath, $updated)
}

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
        foreach ($sample in $samples) {
            $variance += [math]::Pow($sample - $mean, 2)
        }
        $standardDeviation = [math]::Sqrt($variance / $samples.Length)
        Write-Host ("PHYXEL_FIRE_OPEN_{0} {1} mean={2:F3} stddev={3:F3} min={4:F3} max={5:F3}" -f `
            $prefix, $name, $mean, $standardDeviation, $minimum, $maximum)
    }
}

function Run-State([string]$label, [bool]$targetXEnabled) {
    Set-TargetX $targetXEnabled
    dotnet build Phyxel.sln -c Debug --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $values = @{}
    $pressure = @{}
    $artifactDirectory = Join-Path $repoRoot ("artifacts\fire-open-" + $label)
    New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
    $rawReport = Join-Path $artifactDirectory 'fire-open-diagnostics-runs.log'
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
        $runExitCode = $LASTEXITCODE
        Add-Content -Path $rawReport -Value ("=== run $run seed=$runSeed ===")
        $output | Add-Content -Path $rawReport

        $line = $output | Where-Object { $_ -like 'PHYXEL_FIRE_OPEN *' } | Select-Object -Last 1
        if (-not $line) { throw "fire_open $label run $run produced no diagnostic line" }
        $scalarPart = ($line -split ' airPressure=', 2)[0]
        Write-Host "PHYXEL_FIRE_OPEN_RUN label=$label run=$run seed=$runSeed exitCode=$runExitCode $($scalarPart.Substring('PHYXEL_FIRE_OPEN '.Length))"
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
    }

    Write-Summary $values ($label + '_SUMMARY')
    Write-Summary $pressure ($label + '_PRESSURE_SUMMARY')
    Write-Host "PHYXEL_FIRE_OPEN_RAW_REPORT label=$label path=$rawReport"
}

try {
    Run-State 'targetx-on' $true
    Run-State 'targetx-off' $false
}
finally {
    # The task is comparative diagnostics; restore precisely the state that was present.
    [System.IO.File]::WriteAllText($shaderPath, $originalShader)
}
