<#
.SYNOPSIS
    Water gate: runs the acceptance scenarios that protect the hand-tuned
    liquid and granular physics, then prints a PASS/FAIL summary table.

.DESCRIPTION
    Unlike RunCorePhaseAcceptance.ps1 this script does NOT stop at the first
    failure. It runs the whole set to the end so the full regression picture is
    visible, and only then returns a non-zero exit code.

    The gate must be green before every commit that touches physics.

    This file is intentionally pure ASCII so that it parses identically under
    Windows PowerShell 5.1 (which reads BOM-less .ps1 as ANSI) and PowerShell 7.
    Do not add non-ASCII characters to this file.

.EXAMPLE
    .\Diagnostics\RunWaterGate.ps1
    Full run against the Debug build.

.EXAMPLE
    .\Diagnostics\RunWaterGate.ps1 -Only sand
    Quick single-scenario smoke check.

.EXAMPLE
    .\Diagnostics\RunWaterGate.ps1 -ArtifactRoot artifacts/water-gate-baseline
    Record the reference baseline.
#>
param(
    [string]$Configuration = 'Debug',
    [string]$ArtifactRoot = 'artifacts/water-gate',
    [string[]]$Only = @(),
    [switch]$IncludeSavedScenes,
    [string]$SavedScenePath = '',
    [string]$Scale = '0.25',
    [int]$TargetFps = 60,
    [switch]$KeepGoingOnCrash
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $repository "bin/$Configuration/net8.0-windows/Phyxel.exe"
$artifactDirectory = [System.IO.Path]::GetFullPath((Join-Path $repository $ArtifactRoot))

if (-not (Test-Path -LiteralPath $executable)) {
    throw "Phyxel executable not found: $executable. Build it first: dotnet build Phyxel.sln -c $Configuration"
}
[System.IO.Directory]::CreateDirectory($artifactDirectory) | Out-Null

# Order matters: cheap scenarios first so a gross breakage shows up quickly.
# Group is only for report readability.
# Native = $true means the scenario requires native resolution
# (AcceptanceRegressionHarness.RequiresNativeResolution). For those the scale
# is NOT forced, otherwise the harness gets a resolution its validator was
# not calibrated for.
# AcceptanceMaterials = $true means the scenario builds its scene from
# acceptance-only materials such as 'test:granular'. Those JSON files are not
# part of the shipped material set, so PHYXEL_MATERIALS_PATH must point at
# Diagnostics/AcceptanceMaterials or the harness dies with KeyNotFoundException.
$catalogue = @(
    [pscustomobject]@{ Label = 'sand';                  Mode = 'sand';                       Group = 'granular';  Saved = $false; Native = $false; AcceptanceMaterials = $false }
    [pscustomobject]@{ Label = 'slope';                 Mode = 'slope';                      Group = 'granular';  Saved = $false; Native = $false; AcceptanceMaterials = $false }
    [pscustomobject]@{ Label = 'granular_displacement'; Mode = 'granular_displacement';      Group = 'granular';  Saved = $false; Native = $false; AcceptanceMaterials = $true  }
    [pscustomobject]@{ Label = 'buoyancy';              Mode = 'buoyancy';                   Group = 'liquid';    Saved = $false; Native = $false; AcceptanceMaterials = $false }
    [pscustomobject]@{ Label = 'slope_underwater';      Mode = 'underwater_granular';        Group = 'granular';  Saved = $false; Native = $false; AcceptanceMaterials = $true  }
    [pscustomobject]@{ Label = 'granular_barrier_off';  Mode = 'granular_barrier';           Group = 'granular';  Saved = $false; Native = $false; AcceptanceMaterials = $true  }
    [pscustomobject]@{ Label = 'granular_barrier_on';   Mode = 'granular_barrier_hydraulic'; Group = 'granular';  Saved = $false; Native = $false; AcceptanceMaterials = $true  }
    [pscustomobject]@{ Label = 'bowl';                  Mode = 'bowl';                       Group = 'liquid';    Saved = $false; Native = $false; AcceptanceMaterials = $false }
    [pscustomobject]@{ Label = 'hydro';                 Mode = 'hydro';                      Group = 'liquid';    Saved = $false; Native = $false; AcceptanceMaterials = $false }
    [pscustomobject]@{ Label = 'flat_surface';          Mode = 'flat_surface';               Group = 'liquid';    Saved = $false; Native = $false; AcceptanceMaterials = $false }
    [pscustomobject]@{ Label = 'water_drain';           Mode = 'water_drain';                Group = 'liquid';    Saved = $false; Native = $false; AcceptanceMaterials = $false }
    [pscustomobject]@{ Label = 'water_stress';          Mode = 'water_stress';               Group = 'liquid';    Saved = $false; Native = $true;  AcceptanceMaterials = $false }
    [pscustomobject]@{ Label = 'pressure_tube';         Mode = 'pressure_tube';              Group = 'hydraulic'; Saved = $false; Native = $false; AcceptanceMaterials = $false }
    [pscustomobject]@{ Label = 'communicating_vessels'; Mode = 'communicating_vessels';      Group = 'hydraulic'; Saved = $false; Native = $false; AcceptanceMaterials = $false }
    [pscustomobject]@{ Label = 'saved_sand_water';      Mode = 'saved_sand_water';           Group = 'saved';     Saved = $true;  Native = $false; AcceptanceMaterials = $false }
    [pscustomobject]@{ Label = 'saved_pressure';        Mode = 'saved_pressure';             Group = 'saved';     Saved = $true;  Native = $false; AcceptanceMaterials = $false }
    [pscustomobject]@{ Label = 'saved_gravity';         Mode = 'saved_gravity';              Group = 'saved';     Saved = $true;  Native = $false; AcceptanceMaterials = $false }
)

$acceptanceMaterialsPath = Join-Path $repository 'Diagnostics/AcceptanceMaterials'
if (-not (Test-Path -LiteralPath $acceptanceMaterialsPath)) {
    throw "Acceptance materials directory not found: $acceptanceMaterialsPath"
}
$acceptanceMaterialsPath = [System.IO.Path]::GetFullPath($acceptanceMaterialsPath)

# @() keeps this an array even when a single scenario matches.
$selected = @($catalogue | Where-Object {
    if ($_.Saved -and -not $IncludeSavedScenes) {
        return $false
    }
    if ($Only.Count -eq 0) {
        return $true
    }
    return (($Only -contains $_.Label) -or ($Only -contains $_.Mode))
})

if ($selected.Count -eq 0) {
    $available = ($catalogue | ForEach-Object { $_.Label }) -join ', '
    throw "No scenario selected. Available labels: $available"
}

if ($IncludeSavedScenes -and [string]::IsNullOrWhiteSpace($SavedScenePath)) {
    throw 'The saved_* scenarios require -SavedScenePath pointing at a saved scene.'
}

$results = [System.Collections.Generic.List[object]]::new()

function Invoke-WaterGateCase {
    param([pscustomobject]$Case)

    $caseDirectory = Join-Path $artifactDirectory $Case.Label
    [System.IO.Directory]::CreateDirectory($caseDirectory) | Out-Null

    $env:PHYXEL_ACCEPTANCE_MODE = $Case.Mode
    if ($Case.Native) {
        # Let the harness pick native resolution on its own.
        Remove-Item Env:PHYXEL_ACCEPTANCE_SCALE -ErrorAction SilentlyContinue
        $effectiveScale = 'native'
    }
    else {
        $env:PHYXEL_ACCEPTANCE_SCALE = $Scale
        $effectiveScale = $Scale
    }
    $env:PHYXEL_ACCEPTANCE_TARGET_FPS = "$TargetFps"
    $env:PHYXEL_ARTIFACT_DIR = $caseDirectory
    if ($Case.AcceptanceMaterials) {
        $env:PHYXEL_MATERIALS_PATH = $acceptanceMaterialsPath
    }
    else {
        Remove-Item Env:PHYXEL_MATERIALS_PATH -ErrorAction SilentlyContinue
    }
    Remove-Item Env:PHYXEL_CORE_MATERIALS_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:PHYXEL_ACCEPTANCE_HYDRAULICS -ErrorAction SilentlyContinue
    Remove-Item Env:PHYXEL_ACCEPTANCE_CAPTURE_FRAME -ErrorAction SilentlyContinue
    if ($Case.Saved) {
        $env:PHYXEL_VERIFY_SCENE_PATH = $SavedScenePath
    }
    else {
        Remove-Item Env:PHYXEL_VERIFY_SCENE_PATH -ErrorAction SilentlyContinue
    }

    Write-Host ''
    Write-Host "=== WATER_GATE_BEGIN $($Case.Label) [$($Case.Mode)] ===" -ForegroundColor Cyan

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $output = @()
    $exitCode = -1
    $crashed = $false

    # A failing scenario is the expected output of a gate, not a script error.
    # Under 'Stop' PowerShell 7.4+ throws on a non-zero native exit code, which
    # would record an honest FAIL as CRASH.
    $previousErrorAction = $ErrorActionPreference
    $previousNativePreference = $null
    $nativePreference = Get-Variable -Name 'PSNativeCommandUseErrorActionPreference' -Scope Global -ErrorAction SilentlyContinue
    $hasNativePreference = ($null -ne $nativePreference)
    if ($hasNativePreference) {
        $previousNativePreference = $Global:PSNativeCommandUseErrorActionPreference
        $Global:PSNativeCommandUseErrorActionPreference = $false
    }
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(& $executable 2>&1 | ForEach-Object { "$_" })
        $exitCode = $LASTEXITCODE
    }
    catch {
        $crashed = $true
        $output = @($output) + @("WATER_GATE_EXCEPTION $($_.Exception.Message)")
    }
    finally {
        $ErrorActionPreference = $previousErrorAction
        if ($hasNativePreference) {
            $Global:PSNativeCommandUseErrorActionPreference = $previousNativePreference
        }
    }
    $stopwatch.Stop()

    $output | ForEach-Object { Write-Host $_ }
    $output | Set-Content -LiteralPath (Join-Path $caseDirectory 'console.log') -Encoding UTF8

    $successMarkers = @($output | Where-Object { $_ -eq 'PHYXEL_ACCEPTANCE_SUCCESS' })
    $failureMarkers = @($output | Where-Object { $_ -eq 'PHYXEL_ACCEPTANCE_FAILED' })
    $succeeded = ($successMarkers.Count -gt 0)
    $failed = ($failureMarkers.Count -gt 0)

    # The harness verdict outranks the exit code and any exception: if the
    # scenario finished and rendered a verdict, that verdict is the gate result.
    if ($failed) {
        $status = 'FAIL'
    }
    elseif ($succeeded -and $exitCode -eq 0) {
        $status = 'PASS'
    }
    elseif ($succeeded) {
        $status = 'PASS_DIRTY'
    }
    elseif ($crashed) {
        $status = 'CRASH'
    }
    else {
        # No marker at all: the scenario never reached validation.
        $status = 'NORESULT'
    }

    # Scenario diagnostic line: the verdict line the validator prints, e.g.
    # 'PHYXEL_D water=... leftTop=...'. This is the value compared against the
    # baseline between stages, so everything environmental must be filtered out:
    # the GPU banner, UI asset counters and harness progress markers are not
    # scenario results. The verdict is printed last, right before METRICS,
    # so take the last surviving line rather than the first.
    $diagnosticLines = @($output | Where-Object {
        $_ -match '^PHYXEL_' -and
        $_ -notmatch '^PHYXEL_GPU' -and
        $_ -notmatch '^PHYXEL_UI_' -and
        $_ -notmatch '^PHYXEL_ACCEPTANCE_' -and
        $_ -notmatch '^PHYXEL_MATERIAL_(ERROR|WARNING)' -and
        $_ -notmatch '^PHYXEL_SCENE_WARNING' -and
        $_ -notmatch '^PHYXEL_WATER_GATE_'
    })
    $diagnostic = ''
    if ($diagnosticLines.Count -gt 0) {
        $diagnostic = $diagnosticLines[$diagnosticLines.Count - 1]
    }

    $metricsLines = @($output | Where-Object { $_ -match '^PHYXEL_ACCEPTANCE_METRICS' })
    $metrics = ''
    if ($metricsLines.Count -gt 0) {
        $metrics = $metricsLines[$metricsLines.Count - 1]
    }

    $elapsedSeconds = [math]::Round($stopwatch.Elapsed.TotalSeconds, 1)

    $row = New-Object psobject
    $row | Add-Member -MemberType NoteProperty -Name 'Label' -Value $Case.Label
    $row | Add-Member -MemberType NoteProperty -Name 'Group' -Value $Case.Group
    $row | Add-Member -MemberType NoteProperty -Name 'Mode' -Value $Case.Mode
    $row | Add-Member -MemberType NoteProperty -Name 'Status' -Value $status
    $row | Add-Member -MemberType NoteProperty -Name 'Scale' -Value $effectiveScale
    $row | Add-Member -MemberType NoteProperty -Name 'ExitCode' -Value $exitCode
    $row | Add-Member -MemberType NoteProperty -Name 'Seconds' -Value $elapsedSeconds
    $row | Add-Member -MemberType NoteProperty -Name 'Diagnostic' -Value $diagnostic
    $row | Add-Member -MemberType NoteProperty -Name 'Metrics' -Value $metrics
    $results.Add($row)

    if ($status -eq 'PASS') {
        $colour = 'Green'
    }
    elseif ($status -eq 'FAIL') {
        $colour = 'Red'
    }
    else {
        $colour = 'Yellow'
    }
    Write-Host "=== WATER_GATE_END $($Case.Label) $status ($elapsedSeconds s) ===" -ForegroundColor $colour

    if ($status -eq 'CRASH' -and -not $KeepGoingOnCrash) {
        throw "Scenario '$($Case.Label)' did not start. That is a build or environment problem, not a physics regression. Re-run with -KeepGoingOnCrash to complete the set anyway."
    }
}

try {
    Write-Host "PHYXEL_WATER_GATE_BEGIN scenarios=$($selected.Count) configuration=$Configuration scale=$Scale" -ForegroundColor Cyan
    foreach ($case in $selected) {
        Invoke-WaterGateCase -Case $case
    }

    $summaryPath = Join-Path $artifactDirectory 'water-gate-summary.csv'
    $results | Export-Csv -LiteralPath $summaryPath -NoTypeInformation -Encoding UTF8

    Write-Host ''
    Write-Host '================ WATER GATE SUMMARY ================' -ForegroundColor Cyan
    $table = $results | Select-Object Label, Group, Status, Scale, Seconds, ExitCode | Format-Table -AutoSize | Out-String
    Write-Host $table

    $passRows = @($results | Where-Object { $_.Status -eq 'PASS' })
    $failRows = @($results | Where-Object { $_.Status -ne 'PASS' })
    $passCount = $passRows.Count
    $failCount = $failRows.Count

    # Markdown fragment for docs/WATER_GATE_BASELINE.md
    $baselineLines = [System.Collections.Generic.List[string]]::new()
    $baselineLines.Add('| Scenario | Group | Status | Diagnostic |')
    $baselineLines.Add('|----------|-------|--------|------------|')
    foreach ($row in $results) {
        $escaped = ($row.Diagnostic -replace '\|', '\|')
        $baselineLines.Add("| ``$($row.Label)`` | $($row.Group) | $($row.Status) | ``$escaped`` |")
    }
    $baselinePath = Join-Path $artifactDirectory 'water-gate-baseline.md'
    $baselineLines | Set-Content -LiteralPath $baselinePath -Encoding UTF8

    Write-Host "Summary CSV:      $summaryPath"
    Write-Host "Markdown table:   $baselinePath"
    Write-Host "Artifacts / PNG:  $artifactDirectory"
    Write-Host ''

    if ($failCount -eq 0) {
        Write-Host "PHYXEL_WATER_GATE_SUCCESS passed=$passCount failed=0" -ForegroundColor Green
        exit 0
    }

    Write-Host "PHYXEL_WATER_GATE_FAILED passed=$passCount failed=$failCount" -ForegroundColor Red
    foreach ($row in $failRows) {
        Write-Host "  ! $($row.Label) => $($row.Status)" -ForegroundColor Red
    }
    exit 1
}
finally {
    @(
        'PHYXEL_ACCEPTANCE_MODE',
        'PHYXEL_ACCEPTANCE_SCALE',
        'PHYXEL_ACCEPTANCE_TARGET_FPS',
        'PHYXEL_ACCEPTANCE_HYDRAULICS',
        'PHYXEL_ACCEPTANCE_CAPTURE_FRAME',
        'PHYXEL_ARTIFACT_DIR',
        'PHYXEL_MATERIALS_PATH',
        'PHYXEL_CORE_MATERIALS_PATH',
        'PHYXEL_VERIFY_SCENE_PATH'
    ) | ForEach-Object { Remove-Item "Env:$_" -ErrorAction SilentlyContinue }
}
