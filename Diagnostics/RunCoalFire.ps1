param([string]$ArtifactRoot='artifacts/coal-fire-20261001/final',[switch]$Single,[switch]$Baseline)
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskRoot=[IO.Path]::GetFullPath((Join-Path $taskRepo $ArtifactRoot))
New-Item -ItemType Directory -Force -Path $taskRoot | Out-Null
$taskExe=Join-Path $taskRepo 'bin/Debug/net8.0-windows/Phyxel.exe'
if($Baseline) {
 $taskRuntime=Join-Path $taskRoot 'baseline-runtime'
 New-Item -ItemType Directory -Force -Path $taskRuntime | Out-Null
 Copy-Item -Path (Join-Path $taskRepo 'bin/Debug/net8.0-windows/*') -Destination $taskRuntime -Recurse -Force
 foreach($taskShader in @('BrushApplication.hlsl','Combustion.hlsl','TransientLifecycle.hlsl','OxidizerShared.hlsli')) {
  $taskSource=@(& git -C $taskRepo show "91fc232:Content/Shaders/$taskShader") -join "`n"
  if($LASTEXITCODE -ne 0) {throw 'Cannot read pre-fix shader'}
  [IO.File]::WriteAllText((Join-Path $taskRuntime "Content/Shaders/$taskShader"),$taskSource,[Text.UTF8Encoding]::new($false))
 }
 $taskExe=Join-Path $taskRuntime 'Phyxel.exe'
}
$taskNames=@('PHYXEL_ACCEPTANCE_MODE','PHYXEL_ACCEPTANCE_SCALE','PHYXEL_ACCEPTANCE_TARGET_FPS','PHYXEL_ACCEPTANCE_AIR','PHYXEL_ACCEPTANCE_OPEN_BOUNDARIES','PHYXEL_ACCEPTANCE_CAPTURE_FRAME','PHYXEL_ARTIFACT_DIR','PHYXEL_MATERIALS_PATH','PHYXEL_CORE_MATERIALS_PATH','PHYXEL_COAL_FIRE_HOLD_SECONDS')
$taskSaved=@{}
foreach($taskName in $taskNames) {$taskSaved[$taskName]=[Environment]::GetEnvironmentVariable($taskName,'Process')}
$taskResults=[Collections.Generic.List[object]]::new()
try {
 $env:PHYXEL_ACCEPTANCE_MODE='coal_fire'; $env:PHYXEL_ACCEPTANCE_SCALE='0.25'; $env:PHYXEL_ACCEPTANCE_OPEN_BOUNDARIES='1'
 $env:PHYXEL_MATERIALS_PATH=$null; $env:PHYXEL_CORE_MATERIALS_PATH=$null; $env:PHYXEL_ACCEPTANCE_CAPTURE_FRAME=$null
 $taskCases=@(@{Label='fps-100-air-1';Fps=100;Air=1})
 if(-not $Single -and -not $Baseline) {$taskCases+=@(@{Label='fps-60-air-1';Fps=60;Air=1},@{Label='fps-30-air-1';Fps=30;Air=1},@{Label='fps-100-air-0';Fps=100;Air=0},@{Label='long-torch';Fps=100;Air=1;Hold=60})}
 foreach($taskCase in $taskCases) {
  $taskDir=Join-Path $taskRoot $taskCase.Label
  New-Item -ItemType Directory -Force -Path $taskDir | Out-Null
  $env:PHYXEL_ACCEPTANCE_TARGET_FPS="$($taskCase.Fps)"; $env:PHYXEL_ACCEPTANCE_AIR="$($taskCase.Air)"; $env:PHYXEL_ARTIFACT_DIR=$taskDir
  $env:PHYXEL_COAL_FIRE_HOLD_SECONDS="$($taskCase.Hold)"
  $taskProcess=Start-Process -FilePath $taskExe -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $taskDir 'run.log') -RedirectStandardError (Join-Path $taskDir 'error.log')
  Get-Content (Join-Path $taskDir 'run.log') | Where-Object {$_ -match '^PHYXEL_(COAL_FIRE|ACCEPTANCE_(SUCCESS|FAILED))'} | Write-Output
  $taskResults.Add([pscustomobject]@{case=$taskCase.Label;passed=($taskProcess.ExitCode -eq 0);exit=$taskProcess.ExitCode})
  $taskResults | Export-Csv -LiteralPath (Join-Path $taskRoot 'summary.csv') -NoTypeInformation
  if($Baseline) {
   if($taskProcess.ExitCode -ne 1 -or -not (Test-Path (Join-Path $taskDir 'report.txt'))) {throw 'Baseline did not reproduce the intended failure'}
  } elseif($taskProcess.ExitCode -ne 0) {Get-Content (Join-Path $taskDir 'error.log'); throw "Failed $($taskCase.Label)"}
 }
} finally {foreach($taskName in $taskNames) {[Environment]::SetEnvironmentVariable($taskName,$taskSaved[$taskName],'Process')}}
