param(
    [string]$Configuration = 'Debug',
    [string]$ArtifactRoot = 'artifacts/phase-full'
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $repository "bin/$Configuration/net8.0-windows/Phyxel.exe"
$phaseMaterials = Join-Path $PSScriptRoot 'PhaseAcceptanceMaterials'
$reorderA = Join-Path $PSScriptRoot 'PhaseReorderA'
$reorderB = Join-Path $PSScriptRoot 'PhaseReorderB'
$artifactDirectory = [System.IO.Path]::GetFullPath((Join-Path $repository $ArtifactRoot))
$results = [System.Collections.Generic.List[object]]::new()
$performance = [System.Collections.Generic.List[string]]::new()

if (-not (Test-Path -LiteralPath $executable)) {
    throw "Phyxel executable not found: $executable"
}
[System.IO.Directory]::CreateDirectory($artifactDirectory) | Out-Null

function Invoke-PhaseCase {
    param(
        [string]$Label,
        [string]$Mode,
        [AllowEmptyString()][string]$MaterialsPath,
        [string]$Scale = '0.25',
        [int]$TargetFps = 60,
        [string]$ScenePath = ''
    )

    $caseDirectory = Join-Path $artifactDirectory $Label
    [System.IO.Directory]::CreateDirectory($caseDirectory) | Out-Null
    $env:PHYXEL_ACCEPTANCE_MODE = $Mode
    $env:PHYXEL_ACCEPTANCE_SCALE = $Scale
    $env:PHYXEL_ACCEPTANCE_TARGET_FPS = "$TargetFps"
    $env:PHYXEL_ARTIFACT_DIR = $caseDirectory
    if ([string]::IsNullOrEmpty($MaterialsPath)) {
        Remove-Item Env:PHYXEL_MATERIALS_PATH -ErrorAction SilentlyContinue
    }
    else {
        $env:PHYXEL_MATERIALS_PATH = $MaterialsPath
    }
    if ([string]::IsNullOrEmpty($ScenePath)) {
        Remove-Item Env:PHYXEL_VERIFY_SCENE_PATH -ErrorAction SilentlyContinue
    }
    else {
        $env:PHYXEL_VERIFY_SCENE_PATH = $ScenePath
    }

    Write-Host "PHYXEL_PHASE_CASE_BEGIN $Label"
    $output = @(& $executable 2>&1 | ForEach-Object { "$_" })
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    $output | Set-Content -LiteralPath (Join-Path $caseDirectory 'console.log') -Encoding UTF8

    $resultLine = $output | Where-Object { $_ -like 'PHASE_ACCEPTANCE_RESULT*' } | Select-Object -Last 1
    if ($exitCode -ne 0 -or $null -eq $resultLine) {
        throw "Phase acceptance '$Label' failed with exit code $exitCode."
    }
    $pattern = '^PHASE_ACCEPTANCE_RESULT scenario=(\S+) passed=(\S+) transitions=(\d+) dispatches=(\d+) maxPerFrame=(\d+) summary=(\S+) fallbackWakeUps=(\d+) summaryReadbacks=(\d+) timingSamples=(\d+) massTemperature=(\S+)$'
    if ($resultLine -notmatch $pattern) {
        throw "Cannot parse phase acceptance result: $resultLine"
    }
    $results.Add([pscustomobject]@{
        Scenario = $Label
        Passed = $Matches[2]
        Transitions = [long]$Matches[3]
        Dispatches = [long]$Matches[4]
        MaximumDispatchesPerFrame = [int]$Matches[5]
        Summary = $Matches[6]
        FallbackWakeUps = [long]$Matches[7]
        SummaryReadbacks = [long]$Matches[8]
        TimingSamples = [int]$Matches[9]
        MassTemperature = $Matches[10]
    })
    foreach ($line in $output | Where-Object { $_ -like 'PHYXEL_PHASE_PERFORMANCE*' }) {
        $performance.Add($line)
    }
    return $output
}

try {
    $functionalModes = @(
        'phase_thresholds',
        'phase_hysteresis',
        'phase_single_transition',
        'phase_normalization_matrix',
        'phase_summary_liquid_gas',
        'phase_summary_solid_liquid',
        'phase_summary_gas_movable',
        'phase_summary_liquid_fixed',
        'phase_wake_gas',
        'phase_wake_liquid',
        'phase_readback_fallback',
        'phase_energy_contract'
    )
    foreach ($mode in $functionalModes) {
        Invoke-PhaseCase -Label $mode -Mode $mode -MaterialsPath $phaseMaterials | Out-Null
    }
    foreach ($fps in 30, 60, 100) {
        Invoke-PhaseCase -Label "phase_pause_continue_${fps}fps" -Mode 'phase_pause_continue' `
            -MaterialsPath $phaseMaterials -TargetFps $fps | Out-Null
    }

    Invoke-PhaseCase -Label 'phase_disabled_registry' -Mode 'phase_disabled_registry' -MaterialsPath '' | Out-Null
    $reorderAOutput = Invoke-PhaseCase -Label 'phase_external_reorder_a' -Mode 'phase_external_reorder' `
        -MaterialsPath $reorderA
    $reorderBOutput = Invoke-PhaseCase -Label 'phase_external_reorder_b' -Mode 'phase_external_reorder' `
        -MaterialsPath $reorderB
    $indexPattern = 'targetRuntimeIndex=(\d+)'
    $indexALine = $reorderAOutput | Where-Object { $_ -like 'PHYXEL_PHASE_REORDER*' } | Select-Object -Last 1
    $indexBLine = $reorderBOutput | Where-Object { $_ -like 'PHYXEL_PHASE_REORDER*' } | Select-Object -Last 1
    if ($indexALine -notmatch $indexPattern) { throw 'Cannot parse reorder A runtime index.' }
    $indexA = [int]$Matches[1]
    if ($indexBLine -notmatch $indexPattern) { throw 'Cannot parse reorder B runtime index.' }
    $indexB = [int]$Matches[1]
    if ($indexA -eq $indexB) {
        throw "Runtime reorder did not move acceptance:external_target (index=$indexA)."
    }

    $roundTripScene = Join-Path $artifactDirectory 'phase-v5-roundtrip.json'
    Invoke-PhaseCase -Label 'phase_v5_roundtrip' -Mode 'phase_v5_roundtrip' `
        -MaterialsPath $phaseMaterials -ScenePath $roundTripScene | Out-Null

    $scales = @(
        @{ Name = '25'; Value = '0.25' },
        @{ Name = '35'; Value = '0.35' },
        @{ Name = '50'; Value = '0.50' },
        @{ Name = '75'; Value = '0.75' },
        @{ Name = '85'; Value = '0.85' },
        @{ Name = '100'; Value = '1.00' }
    )
    foreach ($scale in $scales) {
        foreach ($state in 'steady', 'burst') {
            $mode = "phase_performance_$state"
            Invoke-PhaseCase -Label "${mode}_$($scale.Name)pct" -Mode $mode `
                -MaterialsPath $phaseMaterials -Scale $scale.Value | Out-Null
        }
    }

    $summaryPath = Join-Path $artifactDirectory 'phase-acceptance-summary.csv'
    $results | Export-Csv -LiteralPath $summaryPath -NoTypeInformation -Encoding UTF8
    $performance | Set-Content -LiteralPath (Join-Path $artifactDirectory 'phase-performance.txt') -Encoding UTF8
    $results | Format-Table -AutoSize
    Write-Host "PHYXEL_PHASE_REORDER_OK targetRuntimeIndex=$indexA->$indexB"
    Write-Host "PHYXEL_PHASE_FULL_SUCCESS scenarios=$($results.Count) summary=$summaryPath"
}
finally {
    @(
        'PHYXEL_ACCEPTANCE_MODE',
        'PHYXEL_ACCEPTANCE_SCALE',
        'PHYXEL_ACCEPTANCE_TARGET_FPS',
        'PHYXEL_ARTIFACT_DIR',
        'PHYXEL_MATERIALS_PATH',
        'PHYXEL_VERIFY_SCENE_PATH'
    ) | ForEach-Object { Remove-Item "Env:$_" -ErrorAction SilentlyContinue }
}
