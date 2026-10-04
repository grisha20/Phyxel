param([string]$ArtifactRoot='artifacts/chimney-transport')
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskRoot=[IO.Path]::GetFullPath((Join-Path $taskRepo $ArtifactRoot))
$taskKeys=@('PHYXEL_VERIFY_CHIMNEY_TRANSPORT','PHYXEL_VERIFY_GAS_TILES','PHYXEL_ARTIFACT_DIR','PHYXEL_WINDOWED','PHYXEL_ACCEPTANCE_MODE')
$taskSaved=@{};foreach($taskKey in $taskKeys){$taskSaved[$taskKey]=[Environment]::GetEnvironmentVariable($taskKey);[Environment]::SetEnvironmentVariable($taskKey,$null)}
try {
 foreach($taskTest in @('CHIMNEY_TRANSPORT','GAS_TILES')) {
  $taskDir=Join-Path $taskRoot $taskTest;New-Item -ItemType Directory -Force $taskDir | Out-Null
  $taskFlag="PHYXEL_VERIFY_$taskTest";[Environment]::SetEnvironmentVariable($taskFlag,'1')
  $env:PHYXEL_WINDOWED='1';$env:PHYXEL_ARTIFACT_DIR=$taskDir
  $taskProcess=Start-Process (Join-Path $taskRepo 'bin/Debug/net8.0-windows/Phyxel.exe') -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput "$taskDir/run.log" -RedirectStandardError "$taskDir/error.log"
  Get-Content "$taskDir/run.log" | Where-Object {$_ -match '^PHYXEL_(CHIMNEY_|GAS_TILES_)'}
  [Environment]::SetEnvironmentVariable($taskFlag,$null)
  if($taskProcess.ExitCode -ne 0){throw "Failed $taskTest"}
 }
} finally {foreach($taskKey in $taskKeys){[Environment]::SetEnvironmentVariable($taskKey,$taskSaved[$taskKey])}}
