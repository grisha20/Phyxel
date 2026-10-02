param([string]$ArtifactRoot='artifacts/gas-flow', [string]$ScenePath='', [switch]$Matrix)
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskRoot=[IO.Path]::GetFullPath((Join-Path $taskRepo $ArtifactRoot))
New-Item -ItemType Directory -Force -Path $taskRoot | Out-Null
$taskNames=@('PHYXEL_ACCEPTANCE_MODE','PHYXEL_ACCEPTANCE_SCALE','PHYXEL_ACCEPTANCE_TARGET_FPS','PHYXEL_ACCEPTANCE_AIR','PHYXEL_ACCEPTANCE_OPEN_BOUNDARIES','PHYXEL_ACCEPTANCE_CAPTURE_FRAME','PHYXEL_ARTIFACT_DIR','PHYXEL_AIR_WALL_SHIFT','PHYXEL_AIR_WALL_HORIZONTAL','PHYXEL_VERIFY_SCENE_PATH')
$taskSaved=@{}; foreach($taskName in $taskNames) {$taskSaved[$taskName]=[Environment]::GetEnvironmentVariable($taskName,'Process')}
$taskResults=[Collections.Generic.List[object]]::new()
try {
 $taskCases=@()
 foreach($taskHorizontal in 0,1) {foreach($taskShift in 0,1,2,3) {
  $taskCases+=@{Label="wall-$taskHorizontal-$taskShift";Mode='air_wall';Air=1;Fps=60;Scale='0.25';Horizontal=$taskHorizontal;Shift=$taskShift}
 }}
 $taskCases+=@{Label='co2-cold-hot';Mode='co2_thermal';Air=0;Fps=60;Scale='0.25'}
 $taskCases+=@{Label='transient-heat';Mode='transient_heat';Air=0;Fps=60;Scale='0.25'}
 $taskCases+=@{Label='gas-coflow';Mode='gas_coflow';Air=1;Fps=60;Scale='0.25'}
 if($ScenePath) {
  $taskFpsCases=if($Matrix) {@(30,60,100)} else {@(60)}
  foreach($taskFps in $taskFpsCases) {$taskCases+=@{Label="furnace-$taskFps";Mode='saved_furnace';Air=1;Fps=$taskFps;Scale='0.35'}}
 }
 foreach($taskCase in $taskCases) {
  $taskDir=Join-Path $taskRoot $taskCase.Label; New-Item -ItemType Directory -Force -Path $taskDir | Out-Null
  $env:PHYXEL_ACCEPTANCE_MODE=$taskCase.Mode; $env:PHYXEL_ACCEPTANCE_SCALE=$taskCase.Scale
  $env:PHYXEL_ACCEPTANCE_TARGET_FPS="$($taskCase.Fps)"; $env:PHYXEL_ACCEPTANCE_AIR="$($taskCase.Air)"
  # Open edges erase the first two rows, including a test wall touching the edge.
  # Keep sealed wall fixtures closed; use the original open world for the furnace.
  $env:PHYXEL_ACCEPTANCE_OPEN_BOUNDARIES=if($taskCase.Mode -eq 'saved_furnace') {'1'} else {'0'}
  $env:PHYXEL_ACCEPTANCE_CAPTURE_FRAME=$null
  $env:PHYXEL_AIR_WALL_SHIFT="$($taskCase.Shift)"; $env:PHYXEL_AIR_WALL_HORIZONTAL="$($taskCase.Horizontal)"
  $env:PHYXEL_VERIFY_SCENE_PATH=$ScenePath; $env:PHYXEL_ARTIFACT_DIR=$taskDir
  $taskProcess=Start-Process -FilePath (Join-Path $taskRepo 'bin/Debug/net8.0-windows/Phyxel.exe') -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $taskDir 'run.log') -RedirectStandardError (Join-Path $taskDir 'error.log')
  Get-Content (Join-Path $taskDir 'run.log') | Where-Object {$_ -match '^PHYXEL_(AIR_WALL|CO2_THERMAL|TRANSIENT_HEAT|GAS_COFLOW|SAVED_FURNACE|ACCEPTANCE_(SUCCESS|FAILED))'} | Write-Output
  $taskResults.Add([pscustomobject]@{Case=$taskCase.Label;Passed=($taskProcess.ExitCode -eq 0);Exit=$taskProcess.ExitCode})
  $taskResults | Export-Csv (Join-Path $taskRoot 'summary.csv') -NoTypeInformation
  if($taskProcess.ExitCode -ne 0) {Get-Content (Join-Path $taskDir 'error.log'); throw "Failed $($taskCase.Label)"}
 }
} finally {foreach($taskName in $taskNames) {[Environment]::SetEnvironmentVariable($taskName,$taskSaved[$taskName],'Process')}}
