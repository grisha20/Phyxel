param(
 [Parameter(Mandatory=$true)][string]$ScenePath,
 [string]$ArtifactRoot='artifacts/sandbox-furnace',
 [ValidateSet('sandbox-sealed','sandbox-open','simulation-open')][string[]]$Cases=@('sandbox-sealed','sandbox-open','simulation-open'),
 [int[]]$Fps=@(60),
 [ValidateRange(60,180)][int]$Seconds=60,
 [switch]$Baseline,
 [switch]$RenderEffects,
 [switch]$CoalTiming,
 [string]$Executable='bin/Debug/net8.0-windows/Phyxel.exe'
)
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskRoot=[IO.Path]::GetFullPath((Join-Path $taskRepo $ArtifactRoot))
$taskSource=[IO.Path]::GetFullPath($ScenePath)
$taskWorld=[IO.Path]::ChangeExtension($taskSource,'.world')
$taskScene=Get-Content -LiteralPath $taskSource -Raw | ConvertFrom-Json
$taskBytes=[IO.File]::ReadAllBytes($taskWorld)
# This observer uses the exact geometry and torch coordinates of the user's
# furnace. Reject other worlds instead of silently testing a different scene.
if($taskBytes.Length -lt 28 -or [BitConverter]::ToUInt32($taskBytes,4) -notin @(7,8,9) -or
 [BitConverter]::ToUInt32($taskBytes,8) -ne 672 -or [BitConverter]::ToUInt32($taskBytes,12) -ne 378 -or
 [BitConverter]::ToUInt32($taskBytes,16) -ne 40) {throw 'Expected the user furnace: world v7/v8/v9, 672x378, Grid40'}
$taskMetal=[Array]::IndexOf([string[]]$taskScene.MaterialPalette,'core:metal')
if($taskMetal -lt 0) {throw 'No metal in the source palette'}
$taskSealed=Join-Path $taskRoot 'sealed-scene'
if([IO.Path]::GetFullPath((Join-Path $taskSealed 'scene.json')) -eq $taskSource) {throw 'Artifact path would overwrite source scene'}
New-Item -ItemType Directory -Force -Path $taskSealed | Out-Null
# Modify a copy only. Preserve every original cell outside the lower aperture,
# and preserve all saved oxygen bytes even inside the new wall.
$taskStream=[IO.MemoryStream]::new($taskBytes,$true)
$taskWriter=[IO.BinaryWriter]::new($taskStream)
try {
 for($taskY=330;$taskY -le 347;$taskY++) {for($taskX=473;$taskX -le 493;$taskX++) {
  $taskStream.Position=28+($taskY*672+$taskX)*40
  $taskWriter.Write([uint32]$taskMetal); $taskWriter.Write([single]7.8)
  foreach($taskZero in 1..3) {$taskWriter.Write([single]0)}
  $taskWriter.Write([uint32]1); $taskWriter.Write([uint32]0); $taskWriter.Write([uint32]0)
  $taskWriter.Write([single]20); $taskWriter.Write([single]0)
 }}
 $taskWriter.Flush()
 [IO.File]::WriteAllBytes((Join-Path $taskSealed 'scene.world'),$taskBytes)
} finally {$taskWriter.Dispose(); $taskStream.Dispose()}
$taskScene | Add-Member -NotePropertyName Mode -NotePropertyValue 'Sandbox' -Force
$taskScene | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $taskSealed 'scene.json') -Encoding UTF8
$taskNames=@('PHYXEL_ACCEPTANCE_MODE','PHYXEL_ACCEPTANCE_SIMULATION_MODE','PHYXEL_ACCEPTANCE_SCALE','PHYXEL_ACCEPTANCE_TARGET_FPS','PHYXEL_ACCEPTANCE_AIR','PHYXEL_ACCEPTANCE_OPEN_BOUNDARIES','PHYXEL_ACCEPTANCE_RENDER_EFFECTS','PHYXEL_ACCEPTANCE_RUN_SEED','PHYXEL_ACCEPTANCE_CAPTURE_FRAME','PHYXEL_FURNACE_TORCH_SECONDS','PHYXEL_FURNACE_SEALED_INTAKE','PHYXEL_FURNACE_DURATION_SECONDS','PHYXEL_VERIFY_SCENE_PATH','PHYXEL_ARTIFACT_DIR','PHYXEL_UI_SCREENSHOT_PATH','PHYXEL_WINDOWED','PHYXEL_WINDOW_WIDTH','PHYXEL_WINDOW_HEIGHT','PHYXEL_MATERIALS_PATH','PHYXEL_CORE_MATERIALS_PATH')
$taskNames += 'PHYXEL_FURNACE_COAL_TIMING'
$taskPrevious=@{}; foreach($taskName in $taskNames) {$taskPrevious[$taskName]=[Environment]::GetEnvironmentVariable($taskName,'Process')}
$taskResults=[Collections.Generic.List[object]]::new()
try {
 $taskExe=[IO.Path]::GetFullPath((Join-Path $taskRepo $Executable))
 if($Baseline) {
  if($Cases.Count -ne 1 -or $Cases[0] -ne 'sandbox-sealed') {throw 'Baseline is only for sandbox-sealed'}
  $taskRuntime=Join-Path $taskRoot 'baseline-runtime'
  New-Item -ItemType Directory -Force $taskRuntime | Out-Null
  Copy-Item -Path (Join-Path $taskRepo 'bin/Debug/net8.0-windows/*') -Destination $taskRuntime -Recurse -Force
  $taskShader=@(& git -C $taskRepo show '0583f2a:Content/Shaders/AirSimulation.hlsl') -join "`n"
  if($LASTEXITCODE -ne 0) {throw 'Cannot read original air shader'}
  [IO.File]::WriteAllText((Join-Path $taskRuntime 'Content/Shaders/AirSimulation.hlsl'),$taskShader,[Text.UTF8Encoding]::new($false))
  $taskExe=Join-Path $taskRuntime 'Phyxel.exe'
 }
 $env:PHYXEL_ACCEPTANCE_MODE='saved_furnace'; $env:PHYXEL_ACCEPTANCE_SCALE='0.35'
 $env:PHYXEL_ACCEPTANCE_AIR='1'; $env:PHYXEL_ACCEPTANCE_OPEN_BOUNDARIES='1'
 $env:PHYXEL_ACCEPTANCE_RENDER_EFFECTS=if($RenderEffects){'1'}else{'0'}; $env:PHYXEL_ACCEPTANCE_RUN_SEED='71001'
 $env:PHYXEL_ACCEPTANCE_CAPTURE_FRAME=$null; $env:PHYXEL_FURNACE_TORCH_SECONDS='5'
 $env:PHYXEL_FURNACE_DURATION_SECONDS="$Seconds"
 $env:PHYXEL_FURNACE_COAL_TIMING=if($CoalTiming){'1'}else{$null}
 $env:PHYXEL_UI_SCREENSHOT_PATH=$null; $env:PHYXEL_WINDOWED='1'
 $env:PHYXEL_WINDOW_WIDTH='1280'; $env:PHYXEL_WINDOW_HEIGHT='720'
 $env:PHYXEL_MATERIALS_PATH=$null; $env:PHYXEL_CORE_MATERIALS_PATH=$null
 foreach($taskCase in $Cases) {foreach($taskFps in $Fps) {
  $taskClosed=$taskCase -eq 'sandbox-sealed'
  $env:PHYXEL_FURNACE_SEALED_INTAKE=if($taskClosed) {'1'} else {'0'}
  $env:PHYXEL_VERIFY_SCENE_PATH=if($taskClosed) {Join-Path $taskSealed 'scene.json'} else {$taskSource}
  $env:PHYXEL_ACCEPTANCE_SIMULATION_MODE=if($taskCase -eq 'simulation-open') {'simulation'} else {'sandbox'}
  $env:PHYXEL_ACCEPTANCE_TARGET_FPS="$taskFps"
  $taskDir=Join-Path $taskRoot "$taskCase-$taskFps"; New-Item -ItemType Directory -Force $taskDir | Out-Null
  $env:PHYXEL_ARTIFACT_DIR=$taskDir
  $taskP=Start-Process $taskExe -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput "$taskDir/run.log" -RedirectStandardError "$taskDir/error.log"
  Get-Content "$taskDir/run.log" | Where-Object {$_ -match '^PHYXEL_(SAVED_FURNACE|ACCEPTANCE_(SUCCESS|FAILED))'}
  $taskReport=Get-Content "$taskDir/report.txt" -Raw -ErrorAction SilentlyContinue
  $taskPassed=if($Baseline) {$taskP.ExitCode -eq 1 -and $taskReport -match 'sealedWall=True' -and $taskReport -match 'lateFlow=False'} else {$taskP.ExitCode -eq 0}
  $taskResults.Add([pscustomobject]@{Case=$taskCase;Fps=$taskFps;Baseline=[bool]$Baseline;Passed=$taskPassed;Exit=$taskP.ExitCode})
  $taskResults | Export-Csv "$taskRoot/summary.csv" -NoTypeInformation
  if(-not $taskPassed) {Get-Content "$taskDir/error.log"; throw "Failed $taskCase / $taskFps"}
 }}
} finally {foreach($taskName in $taskNames) {[Environment]::SetEnvironmentVariable($taskName,$taskPrevious[$taskName],'Process')}}
