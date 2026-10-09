param(
 [string]$ArtifactRoot='artifacts/fire-performance',
 [ValidateRange(10,60)][int]$Seconds=20,
 [ValidateRange(1,60)][int]$Radius=5,
 [ValidateRange(.25,1)][double]$Scale=1,
 [switch]$CoalStrip,
 [switch]$OxygenReference,
 [string[]]$Modes=@('Simulation','Sandbox'),
 [string]$Executable='bin/Debug/net8.0-windows/Phyxel.exe'
)
$ErrorActionPreference='Stop'
if(Get-Process Phyxel -ErrorAction SilentlyContinue){throw 'Save and close Phyxel before diagnostics.'}
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskRoot=if([IO.Path]::IsPathRooted($ArtifactRoot)){[IO.Path]::GetFullPath($ArtifactRoot)}else{[IO.Path]::GetFullPath((Join-Path $taskRepo $ArtifactRoot))}
$taskNames=@('PHYXEL_ACCEPTANCE_MODE','PHYXEL_ACCEPTANCE_SIMULATION_MODE','PHYXEL_ACCEPTANCE_SCALE','PHYXEL_ACCEPTANCE_TARGET_FPS','PHYXEL_ACCEPTANCE_REALTIME_CLOCK','PHYXEL_ACCEPTANCE_AIR','PHYXEL_ACCEPTANCE_OPEN_BOUNDARIES','PHYXEL_ACCEPTANCE_RENDER_EFFECTS','PHYXEL_ACCEPTANCE_CAPTURE_FRAME','PHYXEL_ACCEPTANCE_RUN_SEED','PHYXEL_FIRE_PERFORMANCE','PHYXEL_FIRE_PERFORMANCE_RADIUS','PHYXEL_ARTIFACT_DIR','PHYXEL_SIMULATION_CLOCK_TRACE','PHYXEL_CLOCK_TRACE_SECONDS','PHYXEL_UI_SCREENSHOT_PATH','PHYXEL_WINDOWED','PHYXEL_WINDOW_WIDTH','PHYXEL_WINDOW_HEIGHT')
$taskNames += @('PHYXEL_FIRE_COAL_STRIP','PHYXEL_FRAME_TRACE','PHYXEL_PERFORMANCE_FRAME_PACING','PHYXEL_OXYGEN_REFERENCE')
$taskSaved=@{}; foreach($taskName in $taskNames) {$taskSaved[$taskName]=[Environment]::GetEnvironmentVariable($taskName,'Process')}
$taskResults=@()
try {
 $env:PHYXEL_ACCEPTANCE_MODE='fire_open'; $env:PHYXEL_ACCEPTANCE_SCALE=$Scale.ToString([Globalization.CultureInfo]::InvariantCulture)
 $env:PHYXEL_FIRE_COAL_STRIP=if($CoalStrip){'1'}else{$null}
 $env:PHYXEL_PERFORMANCE_FRAME_PACING='1'
 $env:PHYXEL_OXYGEN_REFERENCE=if($OxygenReference){'1'}else{$null}
 $env:PHYXEL_ACCEPTANCE_TARGET_FPS=$null; $env:PHYXEL_ACCEPTANCE_REALTIME_CLOCK='1'
 $env:PHYXEL_ACCEPTANCE_AIR='1'; $env:PHYXEL_ACCEPTANCE_OPEN_BOUNDARIES='1'
 $env:PHYXEL_ACCEPTANCE_RENDER_EFFECTS='1'; $env:PHYXEL_ACCEPTANCE_CAPTURE_FRAME='999999'
 $env:PHYXEL_ACCEPTANCE_RUN_SEED='71001'; $env:PHYXEL_FIRE_PERFORMANCE='1'
 $env:PHYXEL_FIRE_PERFORMANCE_RADIUS="$Radius"
 $env:PHYXEL_CLOCK_TRACE_SECONDS="$Seconds"; $env:PHYXEL_UI_SCREENSHOT_PATH=$null
 $env:PHYXEL_WINDOWED='1'; $env:PHYXEL_WINDOW_WIDTH='1920'; $env:PHYXEL_WINDOW_HEIGHT='1080'
 foreach($taskMode in $Modes) {
  if($taskMode -notin @('Simulation','Sandbox')) {throw 'Unknown mode'}
  $taskDir=Join-Path $taskRoot $taskMode; New-Item -ItemType Directory -Force $taskDir | Out-Null
  $env:PHYXEL_ARTIFACT_DIR=$taskDir; $env:PHYXEL_ACCEPTANCE_SIMULATION_MODE=$taskMode.ToLowerInvariant()
  $env:PHYXEL_FRAME_TRACE=Join-Path $taskDir 'frame.csv'
  $env:PHYXEL_SIMULATION_CLOCK_TRACE=Join-Path $taskDir 'clock.csv'
  $taskProcess=Start-Process (Join-Path $taskRepo $Executable) -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput "$taskDir/run.log" -RedirectStandardError "$taskDir/error.log"
  if($taskProcess.ExitCode -ne 0) {throw "Performance run failed: $taskMode"}
  $taskRows=@(Import-Csv $env:PHYXEL_SIMULATION_CLOCK_TRACE)
  if($taskRows.Count -lt 8) {throw 'Not enough wall-clock samples'}
  $taskFirst=$taskRows[3]; $taskLast=$taskRows[-1]
  $taskWall=[double]$taskLast.wallSeconds-[double]$taskFirst.wallSeconds
  $taskRate=([double]$taskLast.simulationSeconds-[double]$taskFirst.simulationSeconds)/$taskWall
  $taskAir=([double]$taskLast.airTicks-[double]$taskFirst.airTicks)/$taskWall
  $taskFire=([double]$taskLast.combustionTicks-[double]$taskFirst.combustionTicks)/$taskWall
  $taskFps=([double]$taskLast.updates-[double]$taskFirst.updates)/$taskWall
  $taskResults += [pscustomobject]@{Mode=$taskMode;Scale=1;Radius=$Radius;RealFps=$taskFps;SimulationRate=$taskRate;AirHz=$taskAir;CombustionHz=$taskFire;CombustionGpuMs=$taskLast.combustionGpuMs;ThermalGpuMs=$taskLast.thermalGpuMs;AirGpuMs=$taskLast.airGpuMs;AirHeatGpuMs=$taskLast.airHeatGpuMs;GasMotionGpuMs=$taskLast.gasMotionGpuMs;ClockPassed=($taskRate -ge .95 -and $taskRate -le 1.05 -and [Math]::Abs($taskAir-60) -lt 2 -and [Math]::Abs($taskFire-60) -lt 2)}
  $taskResults[-1].Scale=$Scale
  $taskResults | Export-Csv "$taskRoot/summary.csv" -NoTypeInformation
  $taskResults[-1] | Format-List
 }
} finally {foreach($taskName in $taskNames) {[Environment]::SetEnvironmentVariable($taskName,$taskSaved[$taskName],'Process')}}
