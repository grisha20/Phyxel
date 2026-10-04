param([string]$ArtifactRoot='artifacts/water-contact',
    [string]$Executable='bin/Debug/net8.0-windows/Phyxel.exe')
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskRoot=[IO.Path]::GetFullPath((Join-Path $taskRepo $ArtifactRoot))
New-Item -ItemType Directory -Force $taskRoot | Out-Null
$taskNames=@('PHYXEL_VERIFY_WATER_CONTACT','PHYXEL_WINDOWED','PHYXEL_WINDOW_WIDTH',
    'PHYXEL_WINDOW_HEIGHT','PHYXEL_ACCEPTANCE_MODE','PHYXEL_VERIFY_SCENE_PATH',
    'PHYXEL_MATERIALS_PATH','PHYXEL_CORE_MATERIALS_PATH')
$taskSaved=@{}
foreach($taskName in $taskNames) {
    $taskSaved[$taskName]=[Environment]::GetEnvironmentVariable($taskName,'Process')
    [Environment]::SetEnvironmentVariable($taskName,$null,'Process')
}
try {
    $env:PHYXEL_VERIFY_WATER_CONTACT='1'; $env:PHYXEL_WINDOWED='1'
    $env:PHYXEL_WINDOW_WIDTH='800'; $env:PHYXEL_WINDOW_HEIGHT='600'
    $taskP=Start-Process (Join-Path $taskRepo $Executable) -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru `
        -RedirectStandardOutput "$taskRoot/run.log" -RedirectStandardError "$taskRoot/error.log"
    Get-Content "$taskRoot/run.log" | Where-Object {$_ -match '^PHYXEL_(WET|WATER)'}
    if($taskP.ExitCode -ne 0) { Get-Content "$taskRoot/error.log"; throw 'Water contact regression failed' }
} finally {
    foreach($taskName in $taskNames) {[Environment]::SetEnvironmentVariable($taskName,$taskSaved[$taskName],'Process')}
}
