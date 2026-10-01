param([string]$ArtifactRoot='artifacts/gas-cycle-final')
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskRoot=[IO.Path]::GetFullPath((Join-Path $taskRepo $ArtifactRoot))
New-Item -ItemType Directory -Force -Path $taskRoot | Out-Null
& dotnet build (Join-Path $taskRepo 'Phyxel.sln') -c Debug --nologo
if($LASTEXITCODE -ne 0) { throw 'Build failed' }
$taskResults=[Collections.Generic.List[object]]::new()
$taskVariables=@('PHYXEL_ACCEPTANCE_MODE','PHYXEL_ACCEPTANCE_CAPTURE_FRAME',
    'PHYXEL_ACCEPTANCE_AIR','PHYXEL_ACCEPTANCE_SCALE','PHYXEL_ACCEPTANCE_TARGET_FPS',
    'PHYXEL_ACCEPTANCE_RUN_SEED','PHYXEL_ARTIFACT_DIR','PHYXEL_MATERIALS_PATH',
    'PHYXEL_ACCEPTANCE_WORLD_WIDTH','PHYXEL_ACCEPTANCE_WORLD_HEIGHT','PHYXEL_ACCEPTANCE_RENDER_EFFECTS')
$taskPrevious=@{}
foreach($taskVariable in $taskVariables) {
    $taskPrevious[$taskVariable]=[Environment]::GetEnvironmentVariable($taskVariable,'Process')
}
try {
    Remove-Item Env:PHYXEL_MATERIALS_PATH,Env:PHYXEL_ACCEPTANCE_WORLD_WIDTH,Env:PHYXEL_ACCEPTANCE_WORLD_HEIGHT -ErrorAction SilentlyContinue
    $env:PHYXEL_ACCEPTANCE_RENDER_EFFECTS='0'
    foreach($taskCase in @(
        @{Mode='steam_surface';Frames=900;Air=1},
        @{Mode='co2_layer';Frames=3600;Air=0},
        @{Mode='co2_layer';Frames=3600;Air=1},
        @{Mode='steam_cycle';Frames=10800;Air=1}
    )) {
        $taskLabel="$($taskCase.Mode)-air-$($taskCase.Air)"
        $taskDirectory=Join-Path $taskRoot $taskLabel
        New-Item -ItemType Directory -Force -Path $taskDirectory | Out-Null
        $env:PHYXEL_ACCEPTANCE_MODE=$taskCase.Mode
        $env:PHYXEL_ACCEPTANCE_CAPTURE_FRAME="$($taskCase.Frames)"
        $env:PHYXEL_ACCEPTANCE_AIR="$($taskCase.Air)"
        $env:PHYXEL_ACCEPTANCE_SCALE='0.25'
        $env:PHYXEL_ACCEPTANCE_TARGET_FPS='60'
        $env:PHYXEL_ACCEPTANCE_RUN_SEED='71001'
        $env:PHYXEL_ARTIFACT_DIR=$taskDirectory
        $taskProcess=Start-Process -FilePath (Join-Path $taskRepo 'bin/Debug/net8.0-windows/Phyxel.exe') -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $taskDirectory 'run.log') -RedirectStandardError (Join-Path $taskDirectory 'error.log')
        $taskPass=$taskProcess.ExitCode -eq 0
        $taskResults.Add([pscustomobject]@{Case=$taskLabel;Passed=$taskPass;Exit=$taskProcess.ExitCode})
        $taskResults | Export-Csv (Join-Path $taskRoot 'summary.csv') -NoTypeInformation
        Get-Content (Join-Path $taskDirectory 'run.log') | Where-Object {$_ -match '^PHYXEL_(STEAM_SURFACE|CO2_LAYER|STEAM_CYCLE|ACCEPTANCE_SUCCESS)'} | Write-Output
        if(-not $taskPass) { Get-Content (Join-Path $taskDirectory 'error.log'); throw "Failed: $taskLabel" }
}
    Write-Output 'GAS_CYCLE_SUCCESS'
}
finally {
    foreach($taskVariable in $taskVariables) {
        [Environment]::SetEnvironmentVariable($taskVariable,$taskPrevious[$taskVariable],'Process')
    }
}
