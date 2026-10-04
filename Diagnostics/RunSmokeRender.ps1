param([string]$ArtifactRoot='artifacts/smoke-render',[switch]$Dense)
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskRoot=[IO.Path]::GetFullPath((Join-Path $taskRepo $ArtifactRoot))
$taskNames=@('PHYXEL_ACCEPTANCE_MODE','PHYXEL_ACCEPTANCE_SCALE','PHYXEL_ACCEPTANCE_CAPTURE_FRAME','PHYXEL_ACCEPTANCE_TARGET_FPS','PHYXEL_ACCEPTANCE_AIR','PHYXEL_ACCEPTANCE_RENDER_EFFECTS','PHYXEL_ARTIFACT_DIR','PHYXEL_UI_SCREENSHOT_PATH','PHYXEL_SMOKE_RENDER_DENSE')
$taskPrevious=@{}
foreach($taskName in $taskNames) {$taskPrevious[$taskName]=[Environment]::GetEnvironmentVariable($taskName,'Process')}
try {
    $env:PHYXEL_ACCEPTANCE_MODE='smoke_render'
    $env:PHYXEL_ACCEPTANCE_SCALE='0.25'
    $env:PHYXEL_ACCEPTANCE_CAPTURE_FRAME=$null
    $env:PHYXEL_ACCEPTANCE_TARGET_FPS='60'
    $env:PHYXEL_ACCEPTANCE_AIR='0'
    $env:PHYXEL_SMOKE_RENDER_DENSE=if($Dense){'1'}else{$null}
    $env:PHYXEL_UI_SCREENSHOT_PATH=$null
    foreach($taskEffects in 0,1) {
        $taskDir=Join-Path $taskRoot "effects-$taskEffects"
        New-Item -ItemType Directory -Force -Path $taskDir | Out-Null
        $env:PHYXEL_ARTIFACT_DIR=$taskDir
        $env:PHYXEL_ACCEPTANCE_RENDER_EFFECTS="$taskEffects"
        $taskP=Start-Process -FilePath (Join-Path $taskRepo 'bin/Debug/net8.0-windows/Phyxel.exe') -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput "$taskDir/run.log" -RedirectStandardError "$taskDir/error.log"
        Get-Content "$taskDir/run.log" | Where-Object {$_ -match '^PHYXEL_(SMOKE_RENDER|ACCEPTANCE_(SUCCESS|FAILED))'}
        if($taskP.ExitCode -ne 0) {Get-Content "$taskDir/error.log"; throw 'Smoke render check failed'}
    }
} finally {foreach($taskName in $taskNames) {[Environment]::SetEnvironmentVariable($taskName,$taskPrevious[$taskName],'Process')}}
