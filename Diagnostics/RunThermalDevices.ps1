param([string]$ArtifactRoot='artifacts/thermal-devices-20261001',[switch]$DevicesOnly)
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskRoot=[IO.Path]::GetFullPath((Join-Path $taskRepo $ArtifactRoot))
$taskMaterials=Join-Path $taskRoot 'insulated-core'
New-Item -ItemType Directory -Force -Path $taskMaterials | Out-Null
Copy-Item -Path (Join-Path $taskRepo 'Materials/core/*.json') -Destination $taskMaterials -Force
$taskFixture=Join-Path $taskMaterials 'fixture.json'
$taskJson=Get-Content -LiteralPath $taskFixture -Raw | ConvertFrom-Json
$taskJson.thermal.conductivity=0
[IO.File]::WriteAllText($taskFixture,($taskJson | ConvertTo-Json -Depth 20),[Text.UTF8Encoding]::new($false))
$taskNames=@('PHYXEL_CORE_MATERIALS_PATH','PHYXEL_MATERIALS_PATH','PHYXEL_ACCEPTANCE_MODE','PHYXEL_ACCEPTANCE_SCALE','PHYXEL_ACCEPTANCE_CAPTURE_FRAME','PHYXEL_ACCEPTANCE_TARGET_FPS','PHYXEL_ACCEPTANCE_AIR','PHYXEL_ACCEPTANCE_RUN_SEED','PHYXEL_ARTIFACT_DIR','PHYXEL_DEVICE_RESTART')
$taskSaved=@{}
foreach($taskName in $taskNames) {$taskSaved[$taskName]=[Environment]::GetEnvironmentVariable($taskName,'Process')}
try {
 $env:PHYXEL_ACCEPTANCE_SCALE='0.25'; $env:PHYXEL_ACCEPTANCE_TARGET_FPS='60'; $env:PHYXEL_ACCEPTANCE_RUN_SEED='71001'
 $taskExternal=Join-Path $taskRoot 'empty-external'
 New-Item -ItemType Directory -Force -Path $taskExternal | Out-Null
 $env:PHYXEL_MATERIALS_PATH=$taskExternal
 $taskCases=@(@{Mode='thermal_devices';Label='devices-60';Frames=900;Fps=60;Air=0;Core=$taskMaterials},
 @{Mode='thermal_devices';Label='devices-30';Frames=450;Fps=30;Air=0;Core=$taskMaterials},
 @{Mode='thermal_devices';Label='devices-100';Frames=1500;Fps=100;Air=0;Core=$taskMaterials},
 @{Mode='thermal_devices';Label='devices-restart';Frames=900;Fps=60;Air=0;Core=$taskMaterials;Restart=(Join-Path $taskRoot 'devices-60/final.scene.json')})
 if(-not $DevicesOnly) {$taskCases+=@{Mode='steam_apparatus';Label='steam_apparatus';Frames=14400;Fps=60;Air=1;Core=(Join-Path $taskRepo 'Materials/core')}}
 foreach($taskCase in $taskCases) {
  $taskDir=Join-Path $taskRoot $taskCase.Label
  New-Item -ItemType Directory -Force -Path $taskDir | Out-Null
  $env:PHYXEL_DEVICE_RESTART=$taskCase.Restart; $env:PHYXEL_ACCEPTANCE_TARGET_FPS="$($taskCase.Fps)"
  $env:PHYXEL_ACCEPTANCE_MODE=$taskCase.Mode; $env:PHYXEL_ACCEPTANCE_CAPTURE_FRAME="$($taskCase.Frames)"
  $env:PHYXEL_CORE_MATERIALS_PATH=$taskCase.Core; $env:PHYXEL_ACCEPTANCE_AIR="$($taskCase.Air)"; $env:PHYXEL_ARTIFACT_DIR=$taskDir
  $taskProcess=Start-Process -FilePath (Join-Path $taskRepo 'bin/Debug/net8.0-windows/Phyxel.exe') -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $taskDir 'run.log') -RedirectStandardError (Join-Path $taskDir 'error.log')
  Get-Content (Join-Path $taskDir 'run.log') | Where-Object {$_ -match '^PHYXEL_(THERMAL_DEVICES|STEAM_CYCLE|ACCEPTANCE_SUCCESS)'} | Write-Output
  if($taskProcess.ExitCode -ne 0) {Get-Content (Join-Path $taskDir 'error.log'); throw "Failed $($taskCase.Mode)"}
 }
} finally {foreach($taskName in $taskNames) {[Environment]::SetEnvironmentVariable($taskName,$taskSaved[$taskName],'Process')}}
