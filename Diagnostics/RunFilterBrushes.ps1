param([string]$ArtifactRoot='artifacts/filter-brushes-20261005')
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskRoot=[IO.Path]::GetFullPath($ArtifactRoot,$taskRepo)
New-Item -ItemType Directory -Force $taskRoot|Out-Null
$taskSaved=@{}
Get-ChildItem Env:PHYXEL_*|ForEach-Object{$taskSaved[$_.Name]=$_.Value;Remove-Item -LiteralPath ('Env:'+$_.Name)}
try {
 foreach($taskCase in @(
  @{Name='model';Flag='FILTER_MODEL';Marker='PHYXEL_FILTER_MODEL_SUCCESS'},
  @{Name='codec';Flag='WORLD_CODEC';Marker='PHYXEL_WORLD_CODEC_SUCCESS'},
  @{Name='ui';Flag='UI';Marker='PHYXEL_UI_REGRESSION_SUCCESS'},
  @{Name='gpu';Flag='FILTERS';Marker='PHYXEL_FILTER_BRUSH_COMPLETE'}
 )) {
  Get-ChildItem Env:PHYXEL_*|ForEach-Object{Remove-Item -LiteralPath ('Env:'+$_.Name)}
  $taskDir=Join-Path $taskRoot $taskCase.Name
  New-Item -ItemType Directory -Force $taskDir|Out-Null
  [Environment]::SetEnvironmentVariable('PHYXEL_VERIFY_'+$taskCase.Flag,'1')
  $env:PHYXEL_WINDOWED='1';$env:PHYXEL_ARTIFACT_DIR=$taskDir
  if($taskCase.Name -eq 'gpu'){$env:PHYXEL_FILTER_BRUSHES_ONLY='1'}
  $taskP=Start-Process "$taskRepo/bin/Debug/net8.0-windows/Phyxel.exe" -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput "$taskDir/run.log" -RedirectStandardError "$taskDir/error.log"
  Get-Content "$taskDir/run.log" -Tail 2
  if($taskP.ExitCode -ne 0 -or -not((Get-Content "$taskDir/run.log")|Select-String -SimpleMatch $taskCase.Marker)) {
   Get-Content "$taskDir/error.log";throw "Filter brush check failed: $($taskCase.Name)"
  }
 }
 foreach($taskView in @(@{Width=1920;Height=1080;Suffix=''},@{Width=1280;Height=720;Suffix='-1280'})) {
 foreach($taskBrush in @('Steam','NoAir')) {
  Get-ChildItem Env:PHYXEL_*|ForEach-Object{Remove-Item -LiteralPath ('Env:'+$_.Name)}
  $env:PHYXEL_WINDOWED='1';$env:PHYXEL_UI_PREVIEW_FILTER=$taskBrush
  $env:PHYXEL_WINDOW_WIDTH=$taskView.Width;$env:PHYXEL_WINDOW_HEIGHT=$taskView.Height
  $env:PHYXEL_UI_SCREENSHOT_PATH=Join-Path $taskRoot "palette$($taskView.Suffix)-$taskBrush.png"
  $taskP=Start-Process "$taskRepo/bin/Debug/net8.0-windows/Phyxel.exe" -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput "$taskRoot/preview$($taskView.Suffix)-$taskBrush.log" -RedirectStandardError "$taskRoot/preview$($taskView.Suffix)-$taskBrush-error.log"
  if($taskP.ExitCode -ne 0 -or !(Test-Path -LiteralPath $env:PHYXEL_UI_SCREENSHOT_PATH)){throw "Palette capture failed: $taskBrush"}
 }
 }
 Write-Output 'PHYXEL_FILTER_BRUSH_CHECKS_COMPLETE'
} finally {
 Get-ChildItem Env:PHYXEL_*|ForEach-Object{Remove-Item -LiteralPath ('Env:'+$_.Name)}
 foreach($taskKey in $taskSaved.Keys){[Environment]::SetEnvironmentVariable($taskKey,$taskSaved[$taskKey])}
}
