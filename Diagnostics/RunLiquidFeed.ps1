param([string]$ArtifactRoot='artifacts/liquid-feed')
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskDir=[IO.Path]::GetFullPath((Join-Path $taskRepo $ArtifactRoot));New-Item -ItemType Directory -Force $taskDir|Out-Null
$taskKeys=@('PHYXEL_VERIFY_LIQUID_FEED','PHYXEL_ARTIFACT_DIR','PHYXEL_WINDOWED','PHYXEL_ACCEPTANCE_MODE','PHYXEL_CORE_MATERIALS_PATH','PHYXEL_MATERIALS_PATH')
$taskSaved=@{};foreach($taskKey in $taskKeys){$taskSaved[$taskKey]=[Environment]::GetEnvironmentVariable($taskKey);[Environment]::SetEnvironmentVariable($taskKey,$null)}
try{
 $env:PHYXEL_VERIFY_LIQUID_FEED='1';$env:PHYXEL_WINDOWED='1';$env:PHYXEL_ARTIFACT_DIR=$taskDir
 $taskP=Start-Process (Join-Path $taskRepo 'bin/Debug/net8.0-windows/Phyxel.exe') -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput "$taskDir/run.log" -RedirectStandardError "$taskDir/error.log"
 Get-Content "$taskDir/run.log" | Where-Object {$_ -like 'PHYXEL_LIQUID_FEED*'}
 if($taskP.ExitCode -ne 0){throw 'Liquid feed checks failed'}
}finally{foreach($taskKey in $taskKeys){[Environment]::SetEnvironmentVariable($taskKey,$taskSaved[$taskKey])}}
