param([string]$ArtifactRoot='artifacts/oxidizer-20261001/final',[switch]$Single,[switch]$Baseline,[switch]$RestartOnly,[switch]$EmptyOnly,[ValidateSet(0,1)][int]$SingleAir=0,[ValidateSet(30,60,100)][int[]]$SingleFps=@(60))
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent $PSScriptRoot
$taskRoot=[IO.Path]::GetFullPath((Join-Path $taskRepo $ArtifactRoot))
$taskCore=Join-Path $taskRoot 'insulated-core'
$taskExternal=Join-Path $taskRoot 'materials'
New-Item -ItemType Directory -Force -Path $taskCore,$taskExternal | Out-Null
Copy-Item -Path (Join-Path $taskRepo 'Materials/core/*.json') -Destination $taskCore -Force
$taskFixture=Join-Path $taskCore 'fixture.json'
$taskJson=Get-Content -LiteralPath $taskFixture -Raw | ConvertFrom-Json
$taskJson.thermal.conductivity=0
[IO.File]::WriteAllText($taskFixture,($taskJson | ConvertTo-Json -Depth 20),[Text.UTF8Encoding]::new($false))
foreach($taskSelf in @(0,1)) {
 $taskId=if($taskSelf) {'test:self_oxygen_fuel'} else {'test:oxygen_fuel'}
 $taskFuel=@{schema=1;id=$taskId;name=@{en='Oxygen test fuel';ru='Oxygen test fuel'};kind='solid';color='#A06030';flags=@();physics=@{density=5;friction=1;flowRate=0};thermal=@{initialTemperature=450;conductivity=0;heatCapacity=1};combustion=@{ignitionTemperature=300;burnRate=0.05;heatPerMass=1;burnedInto='core:empty';spreadRate=3;maximumTemperature=900};ui=@{order=200;hidden=$true}}
 if($taskSelf) {$taskFuel.flags=@('self-oxidizing')}
 [IO.File]::WriteAllText((Join-Path $taskExternal "fuel-$taskSelf.json"),($taskFuel | ConvertTo-Json -Depth 20),[Text.UTF8Encoding]::new($false))
}
$taskNames=@('PHYXEL_CORE_MATERIALS_PATH','PHYXEL_MATERIALS_PATH','PHYXEL_ACCEPTANCE_MODE','PHYXEL_ACCEPTANCE_SCALE','PHYXEL_ACCEPTANCE_CAPTURE_FRAME','PHYXEL_ACCEPTANCE_TARGET_FPS','PHYXEL_ACCEPTANCE_AIR','PHYXEL_ACCEPTANCE_RENDER_EFFECTS','PHYXEL_ARTIFACT_DIR','PHYXEL_OXIDIZER_RESTART','PHYXEL_OXIDIZER_EMPTY_LOAD','PHYXEL_VERIFY_SCENE_PATH','PHYXEL_ACCEPTANCE_OPEN_BOUNDARIES')
$taskSaved=@{}
foreach($taskName in $taskNames) {$taskSaved[$taskName]=[Environment]::GetEnvironmentVariable($taskName,'Process')}
$taskResults=[Collections.Generic.List[object]]::new()
try {
 $env:PHYXEL_ACCEPTANCE_MODE='oxidizer'; $env:PHYXEL_ACCEPTANCE_SCALE='0.25'
 $env:PHYXEL_CORE_MATERIALS_PATH=$taskCore; $env:PHYXEL_MATERIALS_PATH=$taskExternal
 $taskCases=@(@{Label='fps-60';Fps=60;Seconds=60;Air=$SingleAir;Effects=0})
 if($Single) {$taskCases=@(foreach($taskFps in $SingleFps){@{Label="fps-$taskFps";Fps=$taskFps;Seconds=60;Air=$SingleAir;Effects=0}})}
 if(-not $Single -and -not $Baseline) {$taskCases+=@(@{Label='fps-30';Fps=30;Seconds=60;Air=0;Effects=0},@{Label='fps-100';Fps=100;Seconds=60;Air=0;Effects=1},@{Label='air-1';Fps=60;Seconds=60;Air=1;Effects=0},@{Label='restart';Fps=60;Seconds=10;Air=0;Effects=0;Restart=(Join-Path $taskRoot 'fps-60/final.scene.json')})}
 if($RestartOnly) {
  if($Single -or $Baseline) {throw 'RestartOnly cannot be combined with Single or Baseline'}
  $taskCases=@($taskCases | Where-Object {$_.Label -eq 'restart'})
  if(-not (Test-Path -LiteralPath $taskCases[0].Restart)) {throw 'Run fps-60 before restarting'}
 }
 if($EmptyOnly) {
  if($Single -or $Baseline -or $RestartOnly) {throw 'EmptyOnly cannot be combined with other case selectors'}
  $taskCases=@(@{Label='empty-load';Fps=60;Seconds=10;Air=0;Effects=0;Empty=1},@{Label='empty-open-floor';Fps=60;Seconds=10;Air=0;Effects=0;Empty=1;Open=1})
 } elseif(-not $Single -and -not $Baseline -and -not $RestartOnly) {
  $taskCases+=@(@{Label='empty-load';Fps=60;Seconds=10;Air=0;Effects=0;Empty=1},@{Label='empty-open-floor';Fps=60;Seconds=10;Air=0;Effects=0;Empty=1;Open=1})
 }
 $taskExe=Join-Path $taskRepo 'bin/Debug/net8.0-windows/Phyxel.exe'
 if($Baseline) {
  $taskRuntime=Join-Path $taskRoot 'baseline-runtime'
  New-Item -ItemType Directory -Force -Path $taskRuntime | Out-Null
  Copy-Item -Path (Join-Path $taskRepo 'bin/Debug/net8.0-windows/*') -Destination $taskRuntime -Recurse -Force
  # Identical harness and transport observer, previous combustion/lifecycle.
  # They never read the field or submit demand: this isolates the old defect.
  foreach($taskShader in @('Combustion.hlsl','EmissionResolve.hlsl','TransientLifecycle.hlsl')) {
   $taskSource=@(& git -C $taskRepo show "f815ccf:Content/Shaders/$taskShader") -join "`n"
   if($LASTEXITCODE -ne 0) {throw 'Cannot read baseline shader'}
   [IO.File]::WriteAllText((Join-Path $taskRuntime "Content/Shaders/$taskShader"),$taskSource,[Text.UTF8Encoding]::new($false))
  }
  $taskExe=Join-Path $taskRuntime 'Phyxel.exe'
 }
 foreach($taskCase in $taskCases) {
  $taskDir=Join-Path $taskRoot $taskCase.Label
  New-Item -ItemType Directory -Force -Path $taskDir | Out-Null
  $env:PHYXEL_OXIDIZER_RESTART=$taskCase.Restart; $env:PHYXEL_ACCEPTANCE_TARGET_FPS="$($taskCase.Fps)"
  $env:PHYXEL_OXIDIZER_EMPTY_LOAD="$($taskCase.Empty)"; $env:PHYXEL_VERIFY_SCENE_PATH=Join-Path $taskDir 'empty.scene.json'
  $env:PHYXEL_ACCEPTANCE_OPEN_BOUNDARIES="$($taskCase.Open)"
  $env:PHYXEL_ACCEPTANCE_CAPTURE_FRAME="$($taskCase.Seconds*$taskCase.Fps)"
  $env:PHYXEL_ACCEPTANCE_AIR="$($taskCase.Air)"; $env:PHYXEL_ACCEPTANCE_RENDER_EFFECTS="$($taskCase.Effects)"; $env:PHYXEL_ARTIFACT_DIR=$taskDir
  $taskProcess=Start-Process -FilePath $taskExe -WorkingDirectory $taskRepo -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $taskDir 'run.log') -RedirectStandardError (Join-Path $taskDir 'error.log')
  Get-Content (Join-Path $taskDir 'run.log') | Where-Object {$_ -match '^PHYXEL_(OXIDIZER|COMBUSTION_MODES|ACCEPTANCE_SUCCESS)'} | Write-Output
  $taskPassed=$taskProcess.ExitCode -eq 0
  $taskResults.Add([pscustomobject]@{case=$taskCase.Label;passed=$taskPassed;exit=$taskProcess.ExitCode})
  $taskSummary=if($RestartOnly) {'restart-summary.csv'} elseif($EmptyOnly) {'empty-summary.csv'} else {'summary.csv'}
  $taskResults | Export-Csv -LiteralPath (Join-Path $taskRoot $taskSummary) -NoTypeInformation
  if($Baseline) {
   $taskReport=Get-Content (Join-Path $taskDir 'report.txt') -Raw -ErrorAction SilentlyContinue
   if($taskProcess.ExitCode -ne 1 -or $taskReport -notmatch 'PHYXEL_OXIDIZER') {throw 'Baseline did not reproduce the expected physical failure'}
  } elseif(-not $taskPassed) {Get-Content (Join-Path $taskDir 'error.log'); throw "Failed $($taskCase.Label)"}
 }
} finally {foreach($taskName in $taskNames) {[Environment]::SetEnvironmentVariable($taskName,$taskSaved[$taskName],'Process')}}
