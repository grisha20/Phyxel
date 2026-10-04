param([string]$ArtifactRoot='artifacts/scene-files')
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskDir=[IO.Path]::GetFullPath((Join-Path $taskRepo $ArtifactRoot))
New-Item -ItemType Directory -Force $taskDir | Out-Null
$taskKeys=@('PHYXEL_VERIFY_SCENE_FILES','PHYXEL_ARTIFACT_DIR','PHYXEL_WINDOWED','PHYXEL_ACCEPTANCE_MODE',
 'PHYXEL_UI_SCREENSHOT_PATH','PHYXEL_UI_CAPTURE_FRAME','PHYXEL_VERIFY_SCENE_PATH')
$taskSaved=@{};foreach($taskKey in $taskKeys){$taskSaved[$taskKey]=[Environment]::GetEnvironmentVariable($taskKey);[Environment]::SetEnvironmentVariable($taskKey,$null)}
try{
 $env:PHYXEL_VERIFY_SCENE_FILES='1';$env:PHYXEL_WINDOWED='1';$env:PHYXEL_ARTIFACT_DIR=$taskDir
 $env:PHYXEL_UI_SCREENSHOT_PATH=Join-Path $taskDir 'status.png';$env:PHYXEL_UI_CAPTURE_FRAME='1'
 $taskP=Start-Process (Join-Path $taskRepo 'bin/Debug/net8.0-windows/Phyxel.exe') -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput "$taskDir/run.log" -RedirectStandardError "$taskDir/error.log"
 Get-Content "$taskDir/run.log" | Where-Object {$_ -like 'PHYXEL_SCENE_FILES*' -or $_ -like 'PHYXEL_UI_SCREENSHOT*'}
 if($taskP.ExitCode -ne 0){throw 'Scene file checks failed'}
}finally{foreach($taskKey in $taskKeys){[Environment]::SetEnvironmentVariable($taskKey,$taskSaved[$taskKey])}}
