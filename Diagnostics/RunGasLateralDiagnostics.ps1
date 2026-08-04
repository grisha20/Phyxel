param(
    [ValidateRange(3, 3)]
    [int]$Runs = 3
)

$ErrorActionPreference = 'Stop'
$culture = [System.Globalization.CultureInfo]::InvariantCulture
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactRoot = Join-Path $repoRoot 'artifacts\gas-lateral-fire-open'

function Add-Sample([hashtable]$values, [string]$key, [double]$value) {
    if (-not $values.ContainsKey($key)) {
        $values[$key] = [System.Collections.Generic.List[double]]::new()
    }
    $values[$key].Add($value)
}

function Write-Statistics([hashtable]$values) {
    foreach ($key in $values.Keys | Sort-Object) {
        [double[]]$samples = $values[$key].ToArray()
        $mean = ($samples | Measure-Object -Average).Average
        $variance = 0.0
        foreach ($sample in $samples) { $variance += [math]::Pow($sample - $mean, 2) }
        $sigma = [math]::Sqrt($variance / $samples.Length)
        Write-Host ("PHYXEL_GAS_LATERAL_SUMMARY {0} mean={1:F3} sigma={2:F3}" -f $key, $mean, $sigma)
    }
}

dotnet build Phyxel.sln -c Debug --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$values = @{}
$rawLog = Join-Path $artifactRoot 'runs.log'
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
Set-Content -Path $rawLog -Value ''

for ($run = 1; $run -le $Runs; $run++) {
    $seed = 71000 + $run
    $env:PHYXEL_ACCEPTANCE_MODE = 'fire_open'
    $env:PHYXEL_ACCEPTANCE_RUN_SEED = $seed.ToString($culture)
    $env:PHYXEL_ACCEPTANCE_SCALE = '0.25'
    $env:PHYXEL_ACCEPTANCE_TARGET_FPS = '60'
    $env:PHYXEL_ACCEPTANCE_CAPTURE_FRAME = '359'
    $env:PHYXEL_GAS_LATERAL_TRACE = '1'
    $env:PHYXEL_ARTIFACT_DIR = Join-Path $artifactRoot ("run-" + $run)
    $output = & dotnet run --project Phyxel.csproj -c Debug --no-build 2>&1
    $exitCode = $LASTEXITCODE
    Add-Content -Path $rawLog -Value ("=== run $run seed=$seed ===")
    $output | Add-Content -Path $rawLog
    if ($exitCode -ne 0) { throw "fire_open run $run failed with $exitCode" }

    foreach ($line in $output | Where-Object { $_ -like 'PHYXEL_GAS_LATERAL path=*' }) {
        $path = ([regex]::Match($line, 'path=([^\s]+)')).Groups[1].Value
        Write-Host "PHYXEL_GAS_LATERAL_RUN run=$run seed=$seed $line"
        foreach ($match in [regex]::Matches($line, '(?:^|\s)([A-Za-z][A-Za-z0-9]*)=(-?[0-9]+(?:\.[0-9]+)?)')) {
            $name = $match.Groups[1].Value
            if ($name -eq 'samples') { continue }
            Add-Sample $values ($path + '.' + $name) ([double]::Parse($match.Groups[2].Value, $culture))
        }
    }
}

Write-Statistics $values
Write-Host "PHYXEL_GAS_LATERAL_RAW_LOG path=$rawLog"
