param([string]$ArtifactRoot='artifacts/scene-repair-20261005/recheck')
$ErrorActionPreference='Stop'
if(Get-Process Phyxel -ErrorAction SilentlyContinue){throw 'Close Phyxel before running the sequential scene checks.'}
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskRoot=[IO.Path]::GetFullPath($ArtifactRoot,$taskRepo)
$taskSaved=@{}
Get-ChildItem Env:PHYXEL_*|ForEach-Object{$taskSaved[$_.Name]=$_.Value;Remove-Item -LiteralPath ('Env:'+$_.Name)}
try {
 foreach($taskCase in @(
  @{Name='gpu';Flag='FILTERS';Marker='PHYXEL_SCENE_REPAIR_COMPLETE'},
  @{Name='save';Flag='SCENE_FILES';Marker='PHYXEL_SCENE_FILES_SUCCESS'},
  @{Name='ui';Flag='UI';Marker='PHYXEL_UI_REGRESSION_SUCCESS'},
  @{Name='codec';Flag='WORLD_CODEC';Marker='PHYXEL_WORLD_CODEC_SUCCESS'}
 )) {
  Get-ChildItem Env:PHYXEL_*|ForEach-Object{Remove-Item -LiteralPath ('Env:'+$_.Name)}
  $taskDir=Join-Path $taskRoot $taskCase.Name
  New-Item -ItemType Directory -Force $taskDir|Out-Null
  [Environment]::SetEnvironmentVariable('PHYXEL_VERIFY_'+$taskCase.Flag,'1')
  $env:PHYXEL_WINDOWED='1';$env:PHYXEL_ARTIFACT_DIR=$taskDir
  if($taskCase.Name -eq 'gpu'){$env:PHYXEL_SCENE_REPAIR_ONLY='1'}
  $taskP=Start-Process "$taskRepo/bin/Debug/net8.0-windows/Phyxel.exe" -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput "$taskDir/run.log" -RedirectStandardError "$taskDir/error.log"
  Get-Content "$taskDir/run.log" -Tail 2
  if($taskP.ExitCode -ne 0 -or -not((Get-Content "$taskDir/run.log")|Select-String -SimpleMatch $taskCase.Marker)) {
   Get-Content "$taskDir/error.log";throw "Scene check failed: $($taskCase.Name)"
  }
 }
 Write-Output 'PHYXEL_SCENE_REPAIR_CHECKS_COMPLETE'
} finally {
 Get-ChildItem Env:PHYXEL_*|ForEach-Object{Remove-Item -LiteralPath ('Env:'+$_.Name)}
 foreach($taskKey in $taskSaved.Keys){[Environment]::SetEnvironmentVariable($taskKey,$taskSaved[$taskKey])}
}
