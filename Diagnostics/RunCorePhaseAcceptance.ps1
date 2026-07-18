param(
    [string]$Configuration = 'Debug',
    [string]$ArtifactRoot = 'artifacts/core-phase'
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $repository "bin/$Configuration/net8.0-windows/Phyxel.exe"
$artifactDirectory = [System.IO.Path]::GetFullPath((Join-Path $repository $ArtifactRoot))
$results = [System.Collections.Generic.List[object]]::new()

if (-not (Test-Path -LiteralPath $executable)) {
    throw "Phyxel executable not found: $executable"
}
[System.IO.Directory]::CreateDirectory($artifactDirectory) | Out-Null

function Invoke-CorePhaseCase {
    param(
        [string]$Label,
        [string]$Mode,
        [int]$TargetFps = 60,
        [string]$ScenePath = ''
    )

    $caseDirectory = Join-Path $artifactDirectory $Label
    [System.IO.Directory]::CreateDirectory($caseDirectory) | Out-Null
    $env:PHYXEL_ACCEPTANCE_MODE = $Mode
    $env:PHYXEL_ACCEPTANCE_SCALE = '0.25'
    $env:PHYXEL_ACCEPTANCE_TARGET_FPS = "$TargetFps"
    $env:PHYXEL_ARTIFACT_DIR = $caseDirectory
    Remove-Item Env:PHYXEL_MATERIALS_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:PHYXEL_CORE_MATERIALS_PATH -ErrorAction SilentlyContinue
    if ([string]::IsNullOrEmpty($ScenePath)) {
        Remove-Item Env:PHYXEL_VERIFY_SCENE_PATH -ErrorAction SilentlyContinue
    }
    else {
        $env:PHYXEL_VERIFY_SCENE_PATH = $ScenePath
    }

    Write-Host "PHYXEL_CORE_PHASE_CASE_BEGIN $Label"
    $output = @(& $executable 2>&1 | ForEach-Object { "$_" })
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    $output | Set-Content -LiteralPath (Join-Path $caseDirectory 'console.log') -Encoding UTF8
    $resultLine = $output | Where-Object { $_ -like 'PHASE_ACCEPTANCE_RESULT*' } | Select-Object -Last 1
    if ($exitCode -ne 0 -or $null -eq $resultLine) {
        throw "Core phase acceptance '$Label' failed with exit code $exitCode."
    }
    $pattern = '^PHASE_ACCEPTANCE_RESULT scenario=(\S+) passed=(\S+) transitions=(\d+) dispatches=(\d+) maxPerFrame=(\d+) summary=(\S+) fallbackWakeUps=(\d+) summaryReadbacks=(\d+) timingSamples=(\d+) massTemperature=(\S+)$'
    if ($resultLine -notmatch $pattern -or $Matches[2] -ne 'true') {
        throw "Cannot accept core phase result: $resultLine"
    }
    $runtimeLine = $output | Where-Object { $_ -like 'PHYXEL_CORE_RUNTIME_INDICES*' } | Select-Object -Last 1
    if ($null -eq $runtimeLine) {
        throw "Core phase acceptance '$Label' did not report runtime indices."
    }
    $results.Add([pscustomobject]@{
        Scenario = $Label
        Passed = $Matches[2]
        Transitions = [long]$Matches[3]
        Dispatches = [long]$Matches[4]
        MaximumDispatchesPerFrame = [int]$Matches[5]
        Summary = $Matches[6]
        FallbackWakeUps = [long]$Matches[7]
        RuntimeIndices = $runtimeLine
    })
}

try {
    Invoke-CorePhaseCase -Label 'water_ice_steam' -Mode 'water_ice_steam'
    Invoke-CorePhaseCase -Label 'water_ice_steam_motion' -Mode 'water_ice_steam_motion'
    foreach ($fps in 30, 60, 100) {
        Invoke-CorePhaseCase -Label "water_ice_steam_pause_${fps}fps" `
            -Mode 'water_ice_steam_pause' -TargetFps $fps
    }

    $roundTripScene = Join-Path $artifactDirectory 'water-ice-steam-v5.json'
    Invoke-CorePhaseCase -Label 'water_ice_steam_v5_roundtrip' `
        -Mode 'water_ice_steam_v5_roundtrip' -ScenePath $roundTripScene
    $scene = Get-Content -LiteralPath $roundTripScene -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($scene.Version -ne 5) {
        throw "Actual-core round-trip wrote scene version $($scene.Version), expected 5."
    }
    foreach ($id in 'core:water', 'core:ice', 'core:steam') {
        if ($scene.MaterialPalette -notcontains $id) {
            throw "Actual-core v5 palette is missing '$id'."
        }
    }

    $summaryPath = Join-Path $artifactDirectory 'core-phase-summary.csv'
    $results | Export-Csv -LiteralPath $summaryPath -NoTypeInformation -Encoding UTF8
    $results | Format-Table -AutoSize
    Write-Host "PHYXEL_CORE_PHASE_SUCCESS scenarios=$($results.Count) summary=$summaryPath"
}
finally {
    @(
        'PHYXEL_ACCEPTANCE_MODE',
        'PHYXEL_ACCEPTANCE_SCALE',
        'PHYXEL_ACCEPTANCE_TARGET_FPS',
        'PHYXEL_ARTIFACT_DIR',
        'PHYXEL_MATERIALS_PATH',
        'PHYXEL_CORE_MATERIALS_PATH',
        'PHYXEL_VERIFY_SCENE_PATH'
    ) | ForEach-Object { Remove-Item "Env:$_" -ErrorAction SilentlyContinue }
}
