param(
    [ValidateRange(1, 3)]
    [int]$Runs = 3,
    [string]$ArtifactSuffix = ''
)

$ErrorActionPreference = 'Stop'
$culture = [System.Globalization.CultureInfo]::InvariantCulture
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Assert-MaterialsCopyMatchesSource {
    $sourceRoot = Join-Path $repoRoot 'Materials'
    $outputRoot = Join-Path $repoRoot 'bin\Debug\net8.0-windows\Materials'
    if (-not (Test-Path -LiteralPath $outputRoot -PathType Container)) {
        throw "Built Materials directory is missing: $outputRoot"
    }

    $mismatches = [System.Collections.Generic.List[string]]::new()
    Get-ChildItem -LiteralPath $sourceRoot -File -Recurse -Filter '*.json' | ForEach-Object {
        $relative = $_.FullName.Substring($sourceRoot.Length).TrimStart('\', '/')
        $output = Join-Path $outputRoot $relative
        if (-not (Test-Path -LiteralPath $output -PathType Leaf)) {
            $mismatches.Add("missing: $relative")
            return
        }
        $sourceHash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        $outputHash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
        if ($sourceHash -ne $outputHash) {
            $mismatches.Add("content differs: $relative")
        }
    }

    if ($mismatches.Count -gt 0) {
        throw ("Materials source/output mismatch before steam_puff: " + ($mismatches -join '; '))
    }
}

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
Assert-MaterialsCopyMatchesSource

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
