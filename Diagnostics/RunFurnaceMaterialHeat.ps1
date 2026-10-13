param([Parameter(Mandatory=$true)][string]$ArtifactRoot,[switch]$Replay,[string]$Executable='',
 [string]$Scene='')
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
if(Get-Process Phyxel -ErrorAction SilentlyContinue){throw 'Save and close Phyxel before diagnostics.'}
$taskDir=[IO.Path]::GetFullPath($ArtifactRoot,$taskRepo)
New-Item -ItemType Directory -Force $taskDir|Out-Null
$taskSaved=@{}
foreach($taskVariable in Get-ChildItem Env:PHYXEL_*){$taskSaved[$taskVariable.Name]=$taskVariable.Value;Remove-Item -LiteralPath ('Env:'+$taskVariable.Name)}
try {
 $env:PHYXEL_VERIFY_FURNACE_MATERIAL_HEAT='1';$env:PHYXEL_WINDOWED='1';$env:PHYXEL_ARTIFACT_DIR=$taskDir
 if($Replay){
  if(!$Scene){$Scene=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Phyxel/Какого хуя так медленно.json'}
  $env:PHYXEL_FH_REPLAY='1';$env:PHYXEL_SENSOR_SCENE=[IO.Path]::GetFullPath($Scene,$taskRepo)
 }
 $taskExecutable=if($Executable){[IO.Path]::GetFullPath($Executable,$taskRepo)}else{"$taskRepo/bin/Debug/net8.0-windows/Phyxel.exe"}
 $taskProcess=Start-Process $taskExecutable -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput "$taskDir/run.log" -RedirectStandardError "$taskDir/errors.log"
 $taskMarker=if($Replay){'^PHYXEL_FH_REPLAY_COMPLETE'}else{'^PHYXEL_FH_CATALOG_SUCCESS'}
 if($taskProcess.ExitCode -ne 0 -or -not((Get-Content "$taskDir/run.log") -match $taskMarker)){
  Get-Content "$taskDir/run.log" -Tail 8;throw 'Furnace material heat diagnostics failed.'
 }
 Get-Content "$taskDir/run.log" -Tail 1
} finally {
 foreach($taskVariable in Get-ChildItem Env:PHYXEL_*){Remove-Item -LiteralPath ('Env:'+$taskVariable.Name)}
 foreach($taskKey in $taskSaved.Keys){[Environment]::SetEnvironmentVariable($taskKey,$taskSaved[$taskKey],'Process')}
}
