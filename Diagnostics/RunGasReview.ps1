param(
    [string]$ArtifactRoot = 'artifacts/gas-review-final',
    [ValidateRange(1,3)][int]$Runs = 3
)
$ErrorActionPreference = 'Stop'
$taskRepo = Split-Path -Parent $PSScriptRoot
$taskArtifacts = [IO.Path]::GetFullPath((Join-Path $taskRepo $ArtifactRoot))
$taskExe = Join-Path $taskRepo 'bin/Debug/net8.0-windows/Phyxel.exe'
New-Item -ItemType Directory -Force -Path $taskArtifacts | Out-Null
$taskResults = [Collections.Generic.List[object]]::new()

& dotnet build (Join-Path $taskRepo 'Phyxel.sln') -c Debug --nologo
if ($LASTEXITCODE -ne 0) { throw 'Gas review build failed' }

foreach ($taskCheck in @('GAS_BRUSH','WORLD_CODEC','THERMAL_MATERIALS','THERMAL_DIFFUSION','PHASE_MATERIALS','PHASE_RUNTIME','COMBUSTION_MATERIALS')) {
    $taskVariable = 'Env:PHYXEL_VERIFY_' + $taskCheck
    Set-Item $taskVariable '1'
    $taskCpuPreference = $ErrorActionPreference
    try {
        # PowerShell 5 treats expected migration warnings on stderr as errors.
        # Collect them, then decide success from the verifier's process exit.
        $ErrorActionPreference = 'Continue'
        $taskCpuOutput = @(& dotnet (Join-Path $taskRepo 'bin/Debug/net8.0-windows/Phyxel.dll') 2>&1 | ForEach-Object { "$_" })
        $taskCpuExit = $LASTEXITCODE
        $ErrorActionPreference = $taskCpuPreference
        $taskCpuOutput | Set-Content -LiteralPath (Join-Path $taskArtifacts ($taskCheck + '.log')) -Encoding UTF8
        if ($taskCpuExit -ne 0) { throw "CPU regression failed: $taskCheck" }
        Write-Host "GAS_REVIEW cpu=$taskCheck passed=True"
    }
    finally {
        $ErrorActionPreference = $taskCpuPreference
        Remove-Item $taskVariable -ErrorAction SilentlyContinue
    }
}

function Invoke-GasReviewCase([string]$mode, [string]$label, [int]$frames, [int]$fps=60, [int]$air=1, [int]$seed=71001, [bool]$effects=$false, [bool]$external=$false, [bool]$paused=$false) {
    $env:PHYXEL_ACCEPTANCE_MODE=$mode
    $env:PHYXEL_ACCEPTANCE_SCALE='0.25'
    $env:PHYXEL_ACCEPTANCE_TARGET_FPS=$fps.ToString()
    $env:PHYXEL_ACCEPTANCE_CAPTURE_FRAME=$frames.ToString()
    $env:PHYXEL_ACCEPTANCE_AIR=$air.ToString()
    $env:PHYXEL_ACCEPTANCE_RUN_SEED=$seed.ToString()
    $env:PHYXEL_ACCEPTANCE_RENDER_EFFECTS=([int]$effects).ToString()
    $env:PHYXEL_ACCEPTANCE_GAS_BRUSH_PAUSED=([int]$paused).ToString()
    $env:PHYXEL_GAS_VERTICAL_TRACE='1'
    $env:PHYXEL_FIRE_OBSTACLE_PLATE_WIDTH='128'
    $env:PHYXEL_STEAM_PUFF_BRUSH_RADIUS='10'
    $env:PHYXEL_STEAM_PUFF_SPAWN_DENSITY='0.82'
    $env:PHYXEL_ARTIFACT_DIR=Join-Path $taskArtifacts $label
    if ($external) { $env:PHYXEL_MATERIALS_PATH=Join-Path $taskRepo 'Diagnostics/AcceptanceMaterials' }
    else { Remove-Item Env:PHYXEL_MATERIALS_PATH -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Force -Path $env:PHYXEL_ARTIFACT_DIR | Out-Null
    $taskProcess = Start-Process -FilePath $taskExe -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru `
        -RedirectStandardOutput (Join-Path $env:PHYXEL_ARTIFACT_DIR 'stdout.log') `
        -RedirectStandardError (Join-Path $env:PHYXEL_ARTIFACT_DIR 'stderr.log')
    $taskOutput = @(Get-Content (Join-Path $env:PHYXEL_ARTIFACT_DIR 'stdout.log')) +
        @(Get-Content (Join-Path $env:PHYXEL_ARTIFACT_DIR 'stderr.log'))
    $taskExit = $taskProcess.ExitCode
    $taskOutput | Set-Content -LiteralPath (Join-Path $env:PHYXEL_ARTIFACT_DIR 'run.log') -Encoding UTF8
    $taskPass = $taskExit -eq 0 -and $taskOutput -contains 'PHYXEL_ACCEPTANCE_SUCCESS'
    $taskResults.Add([pscustomobject]@{case=$label;passed=$taskPass;exit=$taskExit})
    $taskResults | Export-Csv -LiteralPath (Join-Path $taskArtifacts 'summary.csv') -NoTypeInformation
    Write-Host "GAS_REVIEW case=$label passed=$taskPass exit=$taskExit"
    foreach($taskLine in $taskOutput) {
        if ($taskLine -match '^PHYXEL_(F |GAS_BRUSH_FPS|GAS_UNIFORM_FAILURE)') { Write-Host $taskLine }
        if ($mode -eq 'steam_puff' -and $taskLine -like 'PHYXEL_STEAM_PUFF *') {
            Write-Host (([regex]::Matches($taskLine,'\b(?:steamMass1|steamMass600|centreOffsetX150|centreOffsetY150|sigmaX150|sigmaY150|clippedTop150|riseRate)=[^\s]+') | ForEach-Object Value) -join ' ')
        }
    }
    return $taskOutput
}

$taskHashes=@()
foreach($taskFps in @(30,60,100)) {
    $taskOutput=Invoke-GasReviewCase 'gas_brush_fps' "fps-$taskFps" ($taskFps*4) $taskFps 0
    $taskLine=$taskOutput | Where-Object {$_ -like 'PHYXEL_GAS_BRUSH_FPS *'} | Select-Object -Last 1
    $taskHashes += [regex]::Match($taskLine,'hash=([A-F0-9]+)').Groups[1].Value
}
$taskOutput=Invoke-GasReviewCase 'gas_brush_fps' 'fps-100-effects' 400 100 0 71001 $true
$taskLine=$taskOutput | Where-Object {$_ -like 'PHYXEL_GAS_BRUSH_FPS *'} | Select-Object -Last 1
$taskHashes += [regex]::Match($taskLine,'hash=([A-F0-9]+)').Groups[1].Value
if (($taskHashes | Select-Object -Unique).Count -ne 1 -or $taskHashes[0].Length -ne 64) {
    throw "Physical gas state differs across FPS or render modes: $($taskHashes -join ',')"
}
Write-Host 'GAS_REVIEW fpsAndRenderHashesMatch=True'

for($taskRun=1; $taskRun -le $Runs; $taskRun++) {
    $taskSeed=71000+$taskRun
    $null=Invoke-GasReviewCase 'gas' "co2-$taskRun" 900 60 1 $taskSeed $true
    foreach($taskAir in @(0,1)) { $null=Invoke-GasReviewCase 'steam_puff' "puff-air-$taskAir-run-$taskRun" 600 60 $taskAir $taskSeed $true }
    $null=Invoke-GasReviewCase 'fire_open' "fire-open-$taskRun" 359 60 1 $taskSeed
    $null=Invoke-GasReviewCase 'fire_obstacle' "fire-obstacle-$taskRun" 359 60 1 $taskSeed
}
$null=Invoke-GasReviewCase 'gas_uniform_distribution' 'mixed-gases' 600 60 1 71001 $false $true
$null=Invoke-GasReviewCase 'external_gas' 'external-gas' 900 60 1 71001 $false $true
$null=Invoke-GasReviewCase 'gas_brush_fps' 'paused-gas-edit' 60 60 0 71001 $true $false $true
foreach($taskMode in @('water_ice_steam_motion','water_ice_steam_pause','water_ice_steam_v5_roundtrip')) {
    $null=Invoke-GasReviewCase $taskMode $taskMode 240
}
if (@($taskResults | Where-Object {-not $_.passed}).Count -gt 0) { exit 1 }
Write-Host "GAS_REVIEW_SUCCESS cases=$($taskResults.Count) artifacts=$taskArtifacts"
