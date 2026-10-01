param([string]$ArtifactRoot='artifacts/steam-energy-20261001')
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskRoot=[IO.Path]::GetFullPath((Join-Path $taskRepo $ArtifactRoot))
$taskMaterials=Join-Path $taskRoot 'insulated-core'
New-Item -ItemType Directory -Force -Path $taskMaterials | Out-Null
Copy-Item -Path (Join-Path $taskRepo 'Materials/core/*.json') -Destination $taskMaterials -Force
foreach($taskName in @('steam','fixture')) {
    $taskPath=Join-Path $taskMaterials ($taskName+'.json')
    $taskJson=Get-Content -LiteralPath $taskPath -Raw | ConvertFrom-Json
    if($taskName -eq 'steam') { $taskJson.thermal.PSObject.Properties.Remove('ambientCooling') }
    else { $taskJson.thermal.conductivity=0 }
    [IO.File]::WriteAllText($taskPath,($taskJson | ConvertTo-Json -Depth 20),[Text.UTF8Encoding]::new($false))
}
& dotnet build (Join-Path $taskRepo 'Phyxel.sln') -c Debug --nologo
if($LASTEXITCODE -ne 0) { throw 'Build failed' }
$taskNames=@('PHYXEL_CORE_MATERIALS_PATH','PHYXEL_ACCEPTANCE_MODE','PHYXEL_ACCEPTANCE_SCALE',
    'PHYXEL_ACCEPTANCE_CAPTURE_FRAME','PHYXEL_ACCEPTANCE_TARGET_FPS','PHYXEL_ACCEPTANCE_AIR',
    'PHYXEL_ARTIFACT_DIR','PHYXEL_ACCEPTANCE_RUN_SEED','PHYXEL_MATERIALS_PATH')
$taskSaved=@{}
foreach($taskName in $taskNames) { $taskSaved[$taskName]=[Environment]::GetEnvironmentVariable($taskName,'Process') }
try {
    $env:PHYXEL_CORE_MATERIALS_PATH=$taskMaterials
    $env:PHYXEL_ACCEPTANCE_MODE='steam_energy'
    $env:PHYXEL_ACCEPTANCE_SCALE='0.25'
    $env:PHYXEL_ACCEPTANCE_CAPTURE_FRAME='900'
    $env:PHYXEL_ACCEPTANCE_TARGET_FPS='60'
    $env:PHYXEL_ACCEPTANCE_AIR='0'
    $env:PHYXEL_ACCEPTANCE_RUN_SEED='71001'
    $taskExternal=Join-Path $taskRoot 'empty-external'
    New-Item -ItemType Directory -Force -Path $taskExternal | Out-Null
    $env:PHYXEL_MATERIALS_PATH=$taskExternal
    foreach($taskVariant in @('baseline','current')) {
        $taskDirectory=Join-Path $taskRoot $taskVariant
        New-Item -ItemType Directory -Force -Path $taskDirectory | Out-Null
        $taskExe=Join-Path $taskRepo 'bin/Debug/net8.0-windows/Phyxel.exe'
        if($taskVariant -eq 'baseline') {
            $taskRuntime=Join-Path $taskRoot 'baseline-runtime'
            New-Item -ItemType Directory -Force -Path $taskRuntime | Out-Null
            Copy-Item -Path (Join-Path $taskRepo 'bin/Debug/net8.0-windows/*') -Destination $taskRuntime -Recurse -Force
            # Same diagnostic executable, previous physical shaders. New flag
            # is ignored by those shaders; it is used only for energy readout.
            foreach($taskShader in @('ThermalDiffusion.hlsl','PhaseTransitions.hlsl')) {
                $taskSource=@(& git -C $taskRepo show "e984f4f:Content/Shaders/$taskShader") -join "`n"
                if($LASTEXITCODE -ne 0) { throw 'Cannot read baseline shader' }
                [IO.File]::WriteAllText((Join-Path $taskRuntime "Content/Shaders/$taskShader"),$taskSource,[Text.UTF8Encoding]::new($false))
            }
            $taskExe=Join-Path $taskRuntime 'Phyxel.exe'
        }
        $env:PHYXEL_ARTIFACT_DIR=$taskDirectory
        $taskProcess=Start-Process -FilePath $taskExe -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $taskDirectory 'run.log') -RedirectStandardError (Join-Path $taskDirectory 'error.log')
        $taskReport=@(Get-Content (Join-Path $taskDirectory 'run.log') | Where-Object {$_ -match '^PHYXEL_STEAM_ENERGY'})
        $taskReport | Write-Output
        if($taskReport.Count -ne 1) {
            Get-Content (Join-Path $taskDirectory 'error.log')
            throw "Missing energy measurement: $taskVariant"
        }
        if($taskVariant -eq 'baseline' -and $taskProcess.ExitCode -eq 0) { throw 'Baseline unexpectedly conserves phase energy' }
        if($taskVariant -eq 'current' -and $taskProcess.ExitCode -ne 0) {
            Get-Content (Join-Path $taskDirectory 'error.log')
            throw 'Current GPU energy acceptance failed'
        }
    }
}
finally { foreach($taskName in $taskNames) { [Environment]::SetEnvironmentVariable($taskName,$taskSaved[$taskName],'Process') } }
