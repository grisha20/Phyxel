param(
 [Parameter(Mandatory=$true)][string]$ScenePath,
 [string]$ArtifactRoot='artifacts/simulation-clock',
 [ValidateRange(10,60)][int]$Seconds=15
)
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskRoot=[IO.Path]::GetFullPath((Join-Path $taskRepo $ArtifactRoot))
$taskSource=[IO.Path]::GetFullPath($ScenePath)
$taskScene=Get-Content -LiteralPath $taskSource -Raw | ConvertFrom-Json
# Deliberately use the ordinary variable-step game clock, not the fixed-FPS
# acceptance clock. Loading is read-only; the diagnostic exit never saves.
$taskNames=@('PHYXEL_ACCEPTANCE_MODE','PHYXEL_ACCEPTANCE_SIMULATION_MODE','PHYXEL_ACCEPTANCE_SCALE','PHYXEL_ACCEPTANCE_TARGET_FPS','PHYXEL_ACCEPTANCE_REALTIME_CLOCK','PHYXEL_ACCEPTANCE_AIR','PHYXEL_ACCEPTANCE_OPEN_BOUNDARIES','PHYXEL_ACCEPTANCE_RENDER_EFFECTS','PHYXEL_ACCEPTANCE_CAPTURE_FRAME','PHYXEL_ACCEPTANCE_RUN_SEED','PHYXEL_VERIFY_SCENE_PATH','PHYXEL_ARTIFACT_DIR','PHYXEL_SIMULATION_CLOCK_TRACE','PHYXEL_CLOCK_TRACE_SECONDS','PHYXEL_UI_SCREENSHOT_PATH','PHYXEL_WINDOWED','PHYXEL_WINDOW_WIDTH','PHYXEL_WINDOW_HEIGHT')
$taskSaved=@{}; foreach($taskName in $taskNames) {$taskSaved[$taskName]=[Environment]::GetEnvironmentVariable($taskName,'Process')}
$taskResults=@()
try {
 $env:PHYXEL_ACCEPTANCE_MODE='saved_furnace'; $env:PHYXEL_ACCEPTANCE_SCALE="$($taskScene.Scale)"
 $env:PHYXEL_ACCEPTANCE_TARGET_FPS=$null; $env:PHYXEL_ACCEPTANCE_REALTIME_CLOCK='1'
 $env:PHYXEL_ACCEPTANCE_AIR='1'; $env:PHYXEL_ACCEPTANCE_OPEN_BOUNDARIES='1'
 $env:PHYXEL_ACCEPTANCE_RENDER_EFFECTS='1'; $env:PHYXEL_ACCEPTANCE_CAPTURE_FRAME=$null
 $env:PHYXEL_ACCEPTANCE_RUN_SEED='71001'; $env:PHYXEL_VERIFY_SCENE_PATH=$taskSource
 $env:PHYXEL_CLOCK_TRACE_SECONDS="$Seconds"; $env:PHYXEL_UI_SCREENSHOT_PATH=$null
 $env:PHYXEL_WINDOWED='1'; $env:PHYXEL_WINDOW_WIDTH='1280'; $env:PHYXEL_WINDOW_HEIGHT='720'
 foreach($taskMode in @('Simulation','Sandbox')) {
  $taskDir=Join-Path $taskRoot $taskMode; New-Item -ItemType Directory -Force $taskDir | Out-Null
  $env:PHYXEL_ARTIFACT_DIR=$taskDir; $env:PHYXEL_ACCEPTANCE_SIMULATION_MODE=$taskMode.ToLowerInvariant()
  $env:PHYXEL_SIMULATION_CLOCK_TRACE=Join-Path $taskDir 'clock.csv'
  $taskProcess=Start-Process (Join-Path $taskRepo 'bin/Debug/net8.0-windows/Phyxel.exe') -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput "$taskDir/run.log" -RedirectStandardError "$taskDir/error.log"
  if($taskProcess.ExitCode -ne 0) {throw "Clock run failed: $taskMode"}
  $taskRows=@(Import-Csv $env:PHYXEL_SIMULATION_CLOCK_TRACE)
  if(@($taskRows | Where-Object { $_.mode -ne $taskMode }).Count -gt 0) {throw "Actual clock mode differs from requested $taskMode"}
  if($taskRows.Count -lt 8) {throw 'Not enough wall-clock samples'}
  $taskFirst=$taskRows[3]; $taskLast=$taskRows[-1]
  $taskWall=[double]$taskLast.wallSeconds-[double]$taskFirst.wallSeconds
  $taskRate=([double]$taskLast.simulationSeconds-[double]$taskFirst.simulationSeconds)/$taskWall
  $taskAir=([double]$taskLast.airTicks-[double]$taskFirst.airTicks)/$taskWall
  $taskGas=([double]$taskLast.gasMotionTicks-[double]$taskFirst.gasMotionTicks)/$taskWall
  $taskThermal=([double]$taskLast.thermalTicks-[double]$taskFirst.thermalTicks)/$taskWall
  $taskFire=([double]$taskLast.combustionTicks-[double]$taskFirst.combustionTicks)/$taskWall
  $taskFps=([double]$taskLast.updates-[double]$taskFirst.updates)/$taskWall
  $taskPassed=$taskRate -ge .95 -and $taskRate -le 1.05 -and [Math]::Abs($taskAir-60) -lt 2 -and [Math]::Abs($taskGas-60) -lt 2 -and [Math]::Abs($taskThermal-20) -lt 1 -and [Math]::Abs($taskFire-60) -lt 2
  $taskResults += [pscustomobject]@{Mode=$taskMode;RealFps=$taskFps;SimulationRate=$taskRate;AirHz=$taskAir;GasMotionHz=$taskGas;ThermalHz=$taskThermal;CombustionHz=$taskFire;Passed=$taskPassed}
  $taskResults | Export-Csv "$taskRoot/summary.csv" -NoTypeInformation
  $taskResults[-1] | Format-List
  if(-not $taskPassed) {throw "Physical clock lost time: $taskMode"}
 }
} finally {foreach($taskName in $taskNames) {[Environment]::SetEnvironmentVariable($taskName,$taskSaved[$taskName],'Process')}}
