param([ValidateRange(3, 3)][int]$Runs = 3)

$ErrorActionPreference = 'Stop'
$culture = [System.Globalization.CultureInfo]::InvariantCulture
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactDirectory = Join-Path $root 'artifacts\metal-chimney'
New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null

dotnet build Phyxel.sln -c Debug --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$samples = @{}
function Add-Sample([string]$name, [double]$value) {
    if (-not $samples.ContainsKey($name)) { $samples[$name] = [Collections.Generic.List[double]]::new() }
    $samples[$name].Add($value)
}

$rawReport = Join-Path $artifactDirectory 'metal-chimney-runs.log'
Set-Content $rawReport ''
for ($run = 1; $run -le $Runs; $run++) {
    $seed = 71000 + $run
    $env:PHYXEL_ACCEPTANCE_MODE = 'metal_chimney'
    $env:PHYXEL_ACCEPTANCE_RUN_SEED = $seed.ToString($culture)
    $env:PHYXEL_ACCEPTANCE_SCALE = '0.25'
    $env:PHYXEL_ACCEPTANCE_TARGET_FPS = '60'
    $env:PHYXEL_ACCEPTANCE_CAPTURE_FRAME = '599'
    $env:PHYXEL_GAS_VERTICAL_TRACE = '1'
    $env:PHYXEL_ARTIFACT_DIR = Join-Path $artifactDirectory "run-$run"
    $output = & dotnet run --project Phyxel.csproj -c Debug --no-build 2>&1
    $exitCode = $LASTEXITCODE
    Add-Content $rawReport "=== run=$run seed=$seed ==="
    $output | Add-Content $rawReport
    foreach ($prefix in 'PHYXEL_METAL_CHIMNEY ', 'PHYXEL_METAL_CHIMNEY_MOTION ') {
        $line = $output | Where-Object { $_ -like "$prefix*" } | Select-Object -Last 1
        if (-not $line) { throw "run $run did not emit $prefix" }
        Write-Host "PHYXEL_METAL_CHIMNEY_RUN run=$run seed=$seed exitCode=$exitCode $line"
        foreach ($m in [regex]::Matches($line, '(?:^|\s)([A-Za-z][A-Za-z0-9]*)=(-?[0-9]+(?:\.[0-9]+)?)')) {
            Add-Sample $m.Groups[1].Value ([double]::Parse($m.Groups[2].Value, $culture))
        }
    }
}

foreach ($name in $samples.Keys | Sort-Object) {
    [double[]]$values = $samples[$name].ToArray()
    $mean = ($values | Measure-Object -Average).Average
    $variance = 0.0
    foreach ($value in $values) { $variance += [math]::Pow($value - $mean, 2) }
    $sigma = [math]::Sqrt($variance / $values.Length)
    Write-Host ("PHYXEL_METAL_CHIMNEY_SUMMARY {0} mean={1:F6} sigma={2:F6}" -f $name, $mean, $sigma)
}
Write-Host "PHYXEL_METAL_CHIMNEY_RAW_REPORT $rawReport"
